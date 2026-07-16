package me.vasya.custommobtweaks;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.block.Block;
import org.bukkit.entity.AbstractArrow;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Illusioner;
import org.bukkit.entity.Projectile;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.EntityShootBowEvent;
import org.bukkit.inventory.EntityEquipment;
import org.bukkit.inventory.ItemStack;
import org.bukkit.persistence.PersistentDataContainer;
import org.bukkit.persistence.PersistentDataType;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Folia-safe recursive Illusioner clone mechanic.
 *
 * <p>Every shot may create one short-lived one-health clone. Clones inherit the same ability,
 * but the family is bounded by a per-root active cap, a generation cap and decreasing lifetimes.
 * All entity mutations are executed on the owning entity/region thread. No global polling task is
 * used.</p>
 */
public final class IllusionerCloneManager implements Listener, PluginComponent {
    private static final long LIMIT_LOG_COOLDOWN_MILLIS = 5_000L;

    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey cloneKey;
    private final NamespacedKey rootKey;
    private final NamespacedKey generationKey;
    private final NamespacedKey cloneProjectileKey;

    private final Map<UUID, Set<UUID>> clonesByRoot = new ConcurrentHashMap<>();
    private final Map<UUID, Illusioner> clonesById = new ConcurrentHashMap<>();
    private final Map<UUID, UUID> rootByClone = new ConcurrentHashMap<>();
    private final Map<UUID, AtomicInteger> activeCounts = new ConcurrentHashMap<>();
    private final Map<UUID, ScheduledTask> retirementTasks = new ConcurrentHashMap<>();
    private final Map<UUID, Long> lastLimitLogAt = new ConcurrentHashMap<>();
    private final Set<UUID> retiredRoots = ConcurrentHashMap.newKeySet();
    private final AtomicBoolean running = new AtomicBoolean(false);

    public IllusionerCloneManager(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.cloneKey = new NamespacedKey(plugin, "illusioner_clone");
        this.rootKey = new NamespacedKey(plugin, "illusioner_clone_root");
        this.generationKey = new NamespacedKey(plugin, "illusioner_clone_generation");
        this.cloneProjectileKey = new NamespacedKey(plugin, "illusioner_clone_projectile");
    }

    @Override
    public void start() {
        running.set(true);
        Bukkit.getPluginManager().registerEvents(this, plugin);
    }

    @Override
    public void shutdown() {
        running.set(false);
        HandlerList.unregisterAll(this);

        for (ScheduledTask task : retirementTasks.values()) {
            if (task != null && !task.isCancelled()) {
                task.cancel();
            }
        }
        retirementTasks.clear();

        // During /cmt reload the plugin is still enabled, so retire the old generation of clones
        // on their own entity schedulers. Clones are non-persistent, so a full server stop cannot
        // leave saved illusion entities behind in world data.
        List<Map.Entry<UUID, Illusioner>> snapshot = new ArrayList<>(clonesById.entrySet());
        for (Map.Entry<UUID, Illusioner> entry : snapshot) {
            UUID cloneId = entry.getKey();
            Illusioner clone = entry.getValue();
            if (clone == null) {
                continue;
            }
            UUID rootId = rootByClone.get(cloneId);
            try {
                ScheduledTask scheduled = clone.getScheduler().run(
                        plugin,
                        task -> dissolveAndRemove(clone, "module-reload"),
                        () -> cleanupRetiredClone(cloneId, rootId)
                );
                if (scheduled == null) {
                    cleanupClone(cloneId, rootId);
                }
            } catch (RuntimeException error) {
                cleanupClone(cloneId, rootId);
                plugin.getLogger().fine("[illusioner-clones] shutdown retirement rejected clone="
                        + cloneId + " error=" + error.getClass().getSimpleName());
            }
        }

        clonesByRoot.clear();
        clonesById.clear();
        rootByClone.clear();
        activeCounts.clear();
        retiredRoots.clear();
        lastLimitLogAt.clear();
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onIllusionerShot(EntityShootBowEvent event) {
        if (!plugin.enabled("illusioner-clones") || !(event.getEntity() instanceof Illusioner shooter)) {
            return;
        }
        if (!(event.getProjectile() instanceof AbstractArrow arrow)) {
            return;
        }

        boolean shooterIsClone = isClone(shooter);
        if (shooterIsClone) {
            arrow.getPersistentDataContainer().set(cloneProjectileKey, PersistentDataType.BYTE, (byte) 1);
        }

        int parentGeneration = shooterIsClone ? readGeneration(shooter) : 0;
        int maxGeneration = Math.max(1,
                plugin.getConfig().getInt("illusioner-clones.max-generation", 7));
        if (parentGeneration >= maxGeneration) {
            logGenerationLimit(shooter, parentGeneration, maxGeneration);
            return;
        }

        UUID rootId = shooterIsClone ? readRootId(shooter) : shooter.getUniqueId();
        if (rootId == null || retiredRoots.contains(rootId)) {
            return;
        }

        int perShot = clamp(
                plugin.getConfig().getInt("illusioner-clones.clones-per-shot", 1),
                1,
                4
        );
        for (int index = 0; index < perShot; index++) {
            if (!reserveActiveSlot(rootId)) {
                logActiveLimit(rootId, shooter);
                return;
            }
            spawnClone(shooter, rootId, parentGeneration + 1);
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onCloneDamage(EntityDamageByEntityEvent event) {
        if (!isCloneDamage(event.getDamager())) {
            return;
        }

        double multiplier = Math.max(0.0D,
                plugin.getConfig().getDouble("illusioner-clones.damage-multiplier", 0.35D));
        event.setDamage(event.getDamage() * multiplier);
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onIllusionerDeath(EntityDeathEvent event) {
        if (!(event.getEntity() instanceof Illusioner illusioner)) {
            return;
        }

        if (isClone(illusioner)) {
            event.getDrops().clear();
            event.setDroppedExp(0);
            UUID rootId = readRootId(illusioner);
            cleanupClone(illusioner.getUniqueId(), rootId);
            debug("Illusioner clone died clone=" + illusioner.getUniqueId()
                    + " root=" + rootId
                    + " generation=" + readGeneration(illusioner)
                    + " cause=" + illusioner.getLastDamageCause());
            return;
        }

        retireRoot(illusioner.getUniqueId());
    }

    private void spawnClone(Illusioner parent, UUID rootId, int generation) {
        UUID parentId = parent.getUniqueId();
        List<Location> candidates = buildSpawnCandidates(parent.getLocation());
        if (candidates.isEmpty()) {
            releaseActiveSlot(rootId);
            diagnostic("Illusioner clone spawn skipped root=" + rootId
                    + " parent=" + parentId
                    + " generation=" + generation
                    + " reason=no-candidates");
            return;
        }
        attemptSpawnCandidate(parentId, rootId, generation, candidates, 0);
    }

    /**
     * Checks and spawns on the region that owns the candidate location. This matters when the
     * shooter stands next to a Folia region boundary: nearby blocks must never be inspected or
     * mutated from the shooter's old region thread.
     */
    private void attemptSpawnCandidate(
            UUID parentId,
            UUID rootId,
            int generation,
            List<Location> candidates,
            int candidateIndex
    ) {
        if (!running.get() || retiredRoots.contains(rootId)) {
            releaseActiveSlot(rootId);
            return;
        }
        if (candidateIndex >= candidates.size()) {
            releaseActiveSlot(rootId);
            diagnostic("Illusioner clone spawn skipped root=" + rootId
                    + " parent=" + parentId
                    + " generation=" + generation
                    + " candidates=" + candidates.size()
                    + " reason=no-safe-location");
            return;
        }

        Location candidate = candidates.get(candidateIndex);
        if (candidate.getWorld() == null) {
            attemptSpawnCandidate(parentId, rootId, generation, candidates, candidateIndex + 1);
            return;
        }

        try {
            Bukkit.getRegionScheduler().execute(plugin, candidate, () -> {
                if (!running.get() || retiredRoots.contains(rootId)) {
                    releaseActiveSlot(rootId);
                    return;
                }
                if (!isSafeSpawn(candidate)) {
                    attemptSpawnCandidate(parentId, rootId, generation, candidates, candidateIndex + 1);
                    return;
                }
                createCloneAt(candidate, parentId, rootId, generation);
            });
        } catch (RuntimeException error) {
            releaseActiveSlot(rootId);
            plugin.getLogger().warning("[illusioner-clones] region spawn dispatch failed root=" + rootId
                    + " parent=" + parentId
                    + " generation=" + generation
                    + " candidate=" + candidateIndex
                    + " error=" + error.getClass().getSimpleName()
                    + ": " + String.valueOf(error.getMessage()));
        }
    }

    private void createCloneAt(Location spawnLocation, UUID parentId, UUID rootId, int generation) {
        if (!running.get() || retiredRoots.contains(rootId) || spawnLocation.getWorld() == null) {
            releaseActiveSlot(rootId);
            return;
        }

        Illusioner clone;
        try {
            Entity spawned = spawnLocation.getWorld().spawnEntity(
                    spawnLocation,
                    org.bukkit.entity.EntityType.ILLUSIONER
            );
            if (!(spawned instanceof Illusioner spawnedIllusioner)) {
                if (spawned != null) {
                    spawned.remove();
                }
                releaseActiveSlot(rootId);
                return;
            }
            clone = spawnedIllusioner;
        } catch (RuntimeException error) {
            releaseActiveSlot(rootId);
            plugin.getLogger().warning("[illusioner-clones] spawn failed root=" + rootId
                    + " parent=" + parentId
                    + " generation=" + generation
                    + " error=" + error.getClass().getSimpleName()
                    + ": " + String.valueOf(error.getMessage()));
            return;
        }

        long lifetimeTicks = lifetimeTicks(generation);
        boolean registered = false;
        try {
            if (!running.get() || retiredRoots.contains(rootId) || !clone.isValid()) {
                releaseActiveSlot(rootId);
                if (clone.isValid()) {
                    clone.remove();
                }
                return;
            }
            tagClone(clone, rootId, generation);
            configureClone(clone);
            registerClone(clone, rootId);
            registered = true;
            scheduleRetirement(clone, rootId, lifetimeTicks, "lifetime-expired");
            playSpawnEffect(clone);

            debug("Illusioner clone spawned root=" + rootId
                    + " parent=" + parentId
                    + " clone=" + clone.getUniqueId()
                    + " generation=" + generation
                    + " lifetimeTicks=" + lifetimeTicks
                    + " active=" + activeCount(rootId)
                    + "/" + maxActive());
        } catch (RuntimeException error) {
            if (registered) {
                cleanupClone(clone.getUniqueId(), rootId);
            } else {
                releaseActiveSlot(rootId);
            }
            if (clone.isValid()) {
                clone.remove();
            }
            plugin.getLogger().warning("[illusioner-clones] clone initialization failed root=" + rootId
                    + " parent=" + parentId
                    + " clone=" + clone.getUniqueId()
                    + " generation=" + generation
                    + " error=" + error.getClass().getSimpleName()
                    + ": " + String.valueOf(error.getMessage()));
        }
    }

    private void configureClone(Illusioner clone) {
        double maxHealthValue = Math.max(0.1D,
                plugin.getConfig().getDouble("illusioner-clones.max-health", 1.0D));
        AttributeInstance maxHealth = clone.getAttribute(Attribute.MAX_HEALTH);
        if (maxHealth != null) {
            maxHealth.setBaseValue(maxHealthValue);
            clone.setHealth(Math.min(maxHealthValue, maxHealth.getValue()));
        } else {
            clone.setHealth(Math.min(maxHealthValue, clone.getHealth()));
        }

        clone.setCustomName(null);
        clone.setCustomNameVisible(false);
        clone.setCanPickupItems(false);
        clone.setRemoveWhenFarAway(false);
        clone.setPersistent(false);

        EntityEquipment equipment = clone.getEquipment();
        if (equipment != null) {
            equipment.setItemInMainHand(new ItemStack(Material.BOW));
            equipment.setItemInMainHandDropChance(0.0F);
        }
    }

    private void tagClone(Illusioner clone, UUID rootId, int generation) {
        PersistentDataContainer pdc = clone.getPersistentDataContainer();
        pdc.set(cloneKey, PersistentDataType.BYTE, (byte) 1);
        pdc.set(rootKey, PersistentDataType.STRING, rootId.toString());
        pdc.set(generationKey, PersistentDataType.INTEGER, generation);
    }

    private void retireRoot(UUID rootId) {
        Set<UUID> cloneIds = clonesByRoot.get(rootId);
        int active = activeCount(rootId);
        if ((cloneIds == null || cloneIds.isEmpty()) && active <= 0) {
            return;
        }

        // Mark the root before touching already spawned clones. Pending region-dispatched spawns
        // also hold an active slot and will see this marker, release the slot and abort.
        retiredRoots.add(rootId);
        long delay = Math.max(0L,
                plugin.getConfig().getLong("illusioner-clones.original-death-delay-ticks", 40L));
        List<UUID> snapshot = cloneIds == null ? List.of() : new ArrayList<>(cloneIds);
        diagnostic("Illusioner root died root=" + rootId
                + " clones=" + snapshot.size()
                + " pending=" + Math.max(0, active - snapshot.size())
                + " dissolveDelayTicks=" + delay);

        for (UUID cloneId : snapshot) {
            Illusioner clone = clonesById.get(cloneId);
            if (clone == null) {
                cleanupClone(cloneId, rootId);
                continue;
            }
            scheduleRetirement(clone, rootId, delay, "original-died");
        }
    }

    private void scheduleRetirement(Illusioner clone, UUID rootId, long delayTicks, String reason) {
        UUID cloneId = clone.getUniqueId();
        cancelRetirementTask(cloneId);

        ScheduledTask scheduled;
        if (delayTicks <= 0L) {
            scheduled = clone.getScheduler().run(
                    plugin,
                    task -> {
                        retirementTasks.remove(cloneId, task);
                        dissolveAndRemove(clone, reason);
                    },
                    () -> cleanupRetiredClone(cloneId, rootId)
            );
        } else {
            scheduled = clone.getScheduler().runDelayed(
                    plugin,
                    task -> {
                        retirementTasks.remove(cloneId, task);
                        dissolveAndRemove(clone, reason);
                    },
                    () -> cleanupRetiredClone(cloneId, rootId),
                    delayTicks
            );
        }

        if (scheduled == null) {
            cleanupClone(cloneId, rootId);
            return;
        }
        retirementTasks.put(cloneId, scheduled);
    }

    private void dissolveAndRemove(Illusioner clone, String reason) {
        UUID cloneId = clone.getUniqueId();
        UUID rootId = readRootId(clone);
        int generation = readGeneration(clone);

        if (clone.isValid()) {
            Location center = clone.getLocation().clone().add(0.0D, 1.0D, 0.0D);
            World world = center.getWorld();
            if (world != null) {
                int particles = clamp(
                        plugin.getConfig().getInt("illusioner-clones.dissolve-particles", 24),
                        0,
                        100
                );
                if (particles > 0) {
                    world.spawnParticle(Particle.PORTAL, center, particles, 0.45D, 0.75D, 0.45D, 0.08D);
                    world.spawnParticle(Particle.WITCH, center, Math.max(1, particles / 3), 0.35D, 0.55D, 0.35D, 0.03D);
                }
                world.playSound(clone.getLocation(), Sound.ENTITY_ILLUSIONER_MIRROR_MOVE, 0.8F, 1.35F);
            }
            clone.remove();
        }

        cleanupClone(cloneId, rootId);
        debug("Illusioner clone dissolved root=" + rootId
                + " clone=" + cloneId
                + " generation=" + generation
                + " reason=" + reason
                + " active=" + (rootId == null ? 0 : activeCount(rootId))
                + "/" + maxActive());
    }

    private void playSpawnEffect(Illusioner clone) {
        Location center = clone.getLocation().clone().add(0.0D, 1.0D, 0.0D);
        World world = center.getWorld();
        if (world == null) {
            return;
        }
        int particles = clamp(
                plugin.getConfig().getInt("illusioner-clones.spawn-particles", 30),
                0,
                100
        );
        if (particles > 0) {
            world.spawnParticle(Particle.PORTAL, center, particles, 0.5D, 0.8D, 0.5D, 0.12D);
        }
        world.playSound(clone.getLocation(), Sound.ENTITY_ILLUSIONER_MIRROR_MOVE, 0.9F, 1.15F);
    }

    private List<Location> buildSpawnCandidates(Location origin) {
        World world = origin.getWorld();
        if (world == null) {
            return List.of();
        }

        double minRadius = Math.max(0.5D,
                plugin.getConfig().getDouble("illusioner-clones.spawn-min-radius", 1.5D));
        double maxRadius = Math.max(minRadius,
                plugin.getConfig().getDouble("illusioner-clones.spawn-max-radius", 3.5D));
        int attempts = clamp(
                plugin.getConfig().getInt("illusioner-clones.spawn-location-attempts", 10),
                1,
                32
        );

        List<Location> candidates = new ArrayList<>(attempts * 3 + 1);
        ThreadLocalRandom random = ThreadLocalRandom.current();
        for (int attempt = 0; attempt < attempts; attempt++) {
            double angle = random.nextDouble(0.0D, Math.PI * 2.0D);
            double distance = random.nextDouble(minRadius, maxRadius + 0.0001D);
            Location candidate = origin.clone().add(
                    Math.cos(angle) * distance,
                    0.0D,
                    Math.sin(angle) * distance
            );
            candidate.setYaw(origin.getYaw());
            candidate.setPitch(0.0F);
            candidates.add(candidate);
            candidates.add(candidate.clone().add(0.0D, 1.0D, 0.0D));
            candidates.add(candidate.clone().add(0.0D, -1.0D, 0.0D));
        }
        Location fallback = origin.clone();
        fallback.setPitch(0.0F);
        candidates.add(fallback);
        return candidates;
    }

    private boolean isSafeSpawn(Location location) {
        World world = location.getWorld();
        if (world == null) {
            return false;
        }
        Block feet = world.getBlockAt(location);
        Block head = world.getBlockAt(location.clone().add(0.0D, 1.0D, 0.0D));
        Block ground = world.getBlockAt(location.clone().add(0.0D, -1.0D, 0.0D));
        return feet.isPassable() && head.isPassable() && ground.getType().isSolid();
    }

    private boolean isCloneDamage(Entity damager) {
        if (damager instanceof Illusioner illusioner) {
            return isClone(illusioner);
        }
        return damager instanceof Projectile projectile
                && projectile.getPersistentDataContainer().has(cloneProjectileKey, PersistentDataType.BYTE);
    }

    private boolean reserveActiveSlot(UUID rootId) {
        if (retiredRoots.contains(rootId)) {
            return false;
        }
        AtomicInteger count = activeCounts.computeIfAbsent(rootId, ignored -> new AtomicInteger());
        int maximum = maxActive();
        while (true) {
            int current = count.get();
            if (current >= maximum) {
                return false;
            }
            if (count.compareAndSet(current, current + 1)) {
                return true;
            }
        }
    }

    private void releaseActiveSlot(UUID rootId) {
        AtomicInteger count = activeCounts.get(rootId);
        if (count == null) {
            return;
        }
        int remaining = count.updateAndGet(value -> Math.max(0, value - 1));
        if (remaining == 0) {
            activeCounts.remove(rootId, count);
            Set<UUID> clones = clonesByRoot.get(rootId);
            if (clones == null || clones.isEmpty()) {
                clonesByRoot.remove(rootId);
                retiredRoots.remove(rootId);
                lastLimitLogAt.remove(rootId);
            }
        }
    }

    private void registerClone(Illusioner clone, UUID rootId) {
        UUID cloneId = clone.getUniqueId();
        clonesById.put(cloneId, clone);
        rootByClone.put(cloneId, rootId);
        clonesByRoot.computeIfAbsent(rootId, ignored -> ConcurrentHashMap.newKeySet())
                .add(cloneId);
    }

    private void cleanupRetiredClone(UUID cloneId, UUID rootId) {
        retirementTasks.remove(cloneId);
        removeCloneIndex(cloneId, rootId);
    }

    private void cleanupClone(UUID cloneId, UUID rootId) {
        if (cloneId == null) {
            return;
        }
        cancelRetirementTask(cloneId);
        removeCloneIndex(cloneId, rootId);
    }

    private void removeCloneIndex(UUID cloneId, UUID rootId) {
        if (cloneId == null) {
            return;
        }
        Illusioner removed = clonesById.remove(cloneId);
        UUID indexedRoot = rootByClone.remove(cloneId);
        UUID resolvedRoot = rootId != null ? rootId : indexedRoot;
        if (resolvedRoot == null) {
            return;
        }

        Set<UUID> cloneIds = clonesByRoot.get(resolvedRoot);
        boolean removedFromSet = cloneIds != null && cloneIds.remove(cloneId);
        if (removed != null || removedFromSet) {
            releaseActiveSlot(resolvedRoot);
        }
        if (cloneIds != null && cloneIds.isEmpty()) {
            clonesByRoot.remove(resolvedRoot, cloneIds);
            retiredRoots.remove(resolvedRoot);
            lastLimitLogAt.remove(resolvedRoot);
        }
    }

    private void cancelRetirementTask(UUID cloneId) {
        ScheduledTask previous = retirementTasks.remove(cloneId);
        if (previous != null && !previous.isCancelled()) {
            previous.cancel();
        }
    }

    private boolean isClone(Illusioner illusioner) {
        return illusioner.getPersistentDataContainer().has(cloneKey, PersistentDataType.BYTE);
    }

    private UUID readRootId(Illusioner illusioner) {
        if (illusioner == null) {
            return null;
        }
        String raw = illusioner.getPersistentDataContainer().get(rootKey, PersistentDataType.STRING);
        if (raw == null || raw.isBlank()) {
            return null;
        }
        try {
            return UUID.fromString(raw);
        } catch (IllegalArgumentException ignored) {
            return null;
        }
    }

    private int readGeneration(Illusioner illusioner) {
        if (illusioner == null) {
            return 0;
        }
        return Math.max(0, illusioner.getPersistentDataContainer().getOrDefault(
                generationKey,
                PersistentDataType.INTEGER,
                0
        ));
    }

    private long lifetimeTicks(int generation) {
        long minimum = Math.max(60L,
                plugin.getConfig().getLong("illusioner-clones.minimum-lifetime-ticks", 60L));
        long base = Math.max(minimum,
                plugin.getConfig().getLong("illusioner-clones.base-lifetime-ticks", 300L));
        long reduction = Math.max(0L,
                plugin.getConfig().getLong("illusioner-clones.lifetime-reduction-per-generation-ticks", 40L));
        long reduced = base - Math.max(0L, generation - 1L) * reduction;
        return Math.max(minimum, reduced);
    }

    private int maxActive() {
        return clamp(
                plugin.getConfig().getInt("illusioner-clones.max-active-per-original", 30),
                1,
                100
        );
    }

    private int activeCount(UUID rootId) {
        AtomicInteger count = activeCounts.get(rootId);
        return count == null ? 0 : Math.max(0, count.get());
    }

    private void logActiveLimit(UUID rootId, Illusioner shooter) {
        long now = System.currentTimeMillis();
        Long previous = lastLimitLogAt.put(rootId, now);
        if (previous != null && now - previous < LIMIT_LOG_COOLDOWN_MILLIS) {
            return;
        }
        diagnostic("Illusioner clone cap reached root=" + rootId
                + " shooter=" + shooter.getUniqueId()
                + " active=" + activeCount(rootId)
                + "/" + maxActive()
                + " nextSpawnSkipped=true");
    }

    private void logGenerationLimit(Illusioner shooter, int generation, int maximum) {
        if (!isClone(shooter)) {
            return;
        }
        UUID rootId = readRootId(shooter);
        if (rootId == null) {
            return;
        }
        long now = System.currentTimeMillis();
        Long previous = lastLimitLogAt.put(rootId, now);
        if (previous != null && now - previous < LIMIT_LOG_COOLDOWN_MILLIS) {
            return;
        }
        diagnostic("Illusioner clone generation limit reached root=" + rootId
                + " shooter=" + shooter.getUniqueId()
                + " generation=" + generation
                + "/" + maximum
                + " nextSpawnSkipped=true");
    }

    private int clamp(int value, int minimum, int maximum) {
        return Math.max(minimum, Math.min(maximum, value));
    }

    private void diagnostic(String message) {
        if (plugin.getConfig().getBoolean("messages.debug", false)) {
            plugin.getLogger().info("[illusioner-clones] " + message);
        }
    }

    private void debug(String message) {
        if (plugin.getConfig().getBoolean("messages.debug", false)
                && plugin.getConfig().getBoolean("illusioner-clones.verbose-logs", false)) {
            plugin.getLogger().info("[illusioner-clones] " + message);
        }
    }
}
