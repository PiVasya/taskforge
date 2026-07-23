package me.vasya.custommobtweaks;

import com.destroystokyo.paper.entity.ai.Goal;
import com.destroystokyo.paper.entity.ai.VanillaGoal;
import com.destroystokyo.paper.event.entity.EntityAddToWorldEvent;
import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.Chunk;
import org.bukkit.Color;
import org.bukkit.Location;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Phantom;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.EntityRemoveEvent;
import org.bukkit.event.world.EntitiesLoadEvent;
import org.bukkit.persistence.PersistentDataContainer;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.util.Vector;

import java.util.Collection;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.logging.Level;

/** Creates one short-lived, one-health phantom copy when any real phantom starts a sweep attack. */
public final class PhantomDiveCloneManager implements PluginComponent, Listener {
    private static final Particle.DustTransition PURPLE_AURA = new Particle.DustTransition(
            Color.fromRGB(78, 18, 140),
            Color.fromRGB(196, 74, 255),
            1.10F
    );

    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey cloneKey;
    private final NamespacedKey cloneParentKey;
    private final NamespacedKey clonePurpleKey;
    private final NamespacedKey dragonPhantomKey = new NamespacedKey("taskforge", "dragon_phantom");
    private final Map<UUID, PhantomTracker> trackers = new ConcurrentHashMap<>();
    private final Map<UUID, CloneState> activeClones = new ConcurrentHashMap<>();
    private final Map<UUID, Set<UUID>> clonesByParent = new ConcurrentHashMap<>();
    private final Map<UUID, Integer> pendingByParent = new ConcurrentHashMap<>();
    private final Object spawnLock = new Object();
    private final AtomicInteger pendingTotal = new AtomicInteger();
    private final AtomicBoolean goalApiFallbackLogged = new AtomicBoolean();
    private volatile boolean running;
    private volatile boolean mobGoalsAvailable = true;

    public PhantomDiveCloneManager(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.cloneKey = new NamespacedKey(plugin, "phantom_dive_clone");
        this.cloneParentKey = new NamespacedKey(plugin, "phantom_dive_clone_parent");
        this.clonePurpleKey = new NamespacedKey(plugin, "phantom_dive_clone_purple");
    }

    @Override
    public void start() {
        running = true;
        Bukkit.getPluginManager().registerEvents(this, plugin);
        inspectLoadedPhantoms();
        debug("manager started monitorPeriodTicks=" + configLong("monitor-period-ticks", 1L)
                + " cooldownTicks=" + configLong("cooldown-ticks", 40L)
                + " cloneHealth=" + configDouble("health", 1.0D)
                + " cloneSize=" + configInt("size", 0)
                + " lifetimeTicks=" + configLong("lifetime-ticks", 200L)
                + " maximumActiveTotal=" + configInt("maximum-active-total", 96)
                + " maximumActivePerParent=" + configInt("maximum-active-per-parent", 2)
                + " recursiveCopies=false");
    }

    @Override
    public void shutdown() {
        running = false;
        HandlerList.unregisterAll(this);
        for (PhantomTracker tracker : Set.copyOf(trackers.values())) {
            tracker.stop("shutdown");
        }
        trackers.clear();
        for (CloneState state : Set.copyOf(activeClones.values())) {
            state.stopAndRemove("shutdown");
        }
        activeClones.clear();
        clonesByParent.clear();
        pendingByParent.clear();
        pendingTotal.set(0);
        debug("manager stopped");
    }

    public String diagnosticsSummary() {
        return "phantom-dive-clones=" + (plugin.enabled("phantom-dive-clones") ? "enabled" : "disabled")
                + ",tracked=" + trackers.size()
                + ",activeCopies=" + activeClones.size()
                + ",pending=" + pendingTotal.get()
                + ",goalDetection=" + (mobGoalsAvailable ? "paper-goal" : "velocity-fallback");
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onEntityAdded(EntityAddToWorldEvent event) {
        if (event.getEntity() instanceof Phantom phantom) {
            handleAddedPhantom(phantom, "entity-add");
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onEntitiesLoad(EntitiesLoadEvent event) {
        for (Entity entity : event.getEntities()) {
            if (entity instanceof Phantom phantom) {
                handleAddedPhantom(phantom, "entities-load");
            }
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onPhantomDeath(EntityDeathEvent event) {
        if (!(event.getEntity() instanceof Phantom phantom)) {
            return;
        }
        stopTracker(phantom.getUniqueId(), "death");
        if (isDiveClone(phantom)) {
            event.getDrops().clear();
            event.setDroppedExp(0);
            unregisterClone(phantom.getUniqueId(), "death");
        }
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onEntityRemove(EntityRemoveEvent event) {
        if (!(event.getEntity() instanceof Phantom phantom)) {
            return;
        }
        stopTracker(phantom.getUniqueId(), "remove");
        if (isDiveClone(phantom)) {
            unregisterClone(phantom.getUniqueId(), "remove");
        }
    }

    private void handleAddedPhantom(Phantom phantom, String source) {
        if (!running) {
            return;
        }
        phantom.getScheduler().runDelayed(plugin, task -> {
            if (!running || !phantom.isValid() || phantom.isDead()) {
                return;
            }
            if (isDiveClone(phantom)) {
                if (!activeClones.containsKey(phantom.getUniqueId())) {
                    debug("stale dive copy detected source=" + source
                            + " uuid=" + phantom.getUniqueId() + " action=remove");
                    phantom.remove();
                }
                return;
            }
            observePhantom(phantom, source);
        }, () -> stopTracker(phantom.getUniqueId(), "retired-before-observe"), 1L);
    }

    private void observePhantom(Phantom phantom, String source) {
        if (!running
                || !plugin.enabled("phantom-dive-clones")
                || !phantom.isValid()
                || phantom.isDead()
                || isDiveClone(phantom)) {
            return;
        }
        PhantomTracker created = new PhantomTracker(phantom);
        PhantomTracker existing = trackers.putIfAbsent(phantom.getUniqueId(), created);
        if (existing == null) {
            debug("phantom tracked source=" + source
                    + " uuid=" + phantom.getUniqueId()
                    + " dragonPhantom=" + isDragonPhantom(phantom)
                    + " size=" + phantom.getSize());
            created.start();
        }
    }

    private void inspectLoadedPhantoms() {
        int chunks = 0;
        for (World world : Bukkit.getWorlds()) {
            for (Chunk chunk : world.getLoadedChunks()) {
                chunks++;
                Location regionPoint = new Location(world,
                        (chunk.getX() << 4) + 8.0D,
                        world.getMinHeight() + 1.0D,
                        (chunk.getZ() << 4) + 8.0D);
                Bukkit.getRegionScheduler().execute(plugin, regionPoint, () -> {
                    if (!running) {
                        return;
                    }
                    for (Entity entity : chunk.getEntities()) {
                        if (entity instanceof Phantom phantom) {
                            if (isDiveClone(phantom)) {
                                phantom.remove();
                            } else {
                                observePhantom(phantom, "startup-loaded-chunk");
                            }
                        }
                    }
                });
            }
        }
        debug("startup loaded-phantom inspection scheduled chunks=" + chunks);
    }

    private boolean isSweepAttackRunning(Phantom phantom) {
        if (mobGoalsAvailable) {
            try {
                Collection<Goal<Phantom>> runningGoals = Bukkit.getMobGoals().getRunningGoals(phantom);
                for (Goal<Phantom> goal : runningGoals) {
                    if (VanillaGoal.PHANTOM_SWEEP_ATTACK.equals(goal.getKey())) {
                        return true;
                    }
                }
                return false;
            } catch (UnsupportedOperationException | LinkageError error) {
                mobGoalsAvailable = false;
                logGoalFallback(error);
            } catch (RuntimeException error) {
                mobGoalsAvailable = false;
                logGoalFallback(error);
            }
        }

        LivingEntity target = phantom.getTarget();
        Vector velocity = phantom.getVelocity();
        return target != null && velocity.getY() < -0.06D && velocity.lengthSquared() > 0.08D;
    }

    private void logGoalFallback(Throwable error) {
        if (goalApiFallbackLogged.compareAndSet(false, true)) {
            plugin.getLogger().log(Level.WARNING,
                    "[phantom-dive-clones] Paper MobGoals sweep detection unavailable; using velocity fallback", error);
        }
    }

    private void spawnDiveClone(Phantom parent) {
        LivingEntity target = parent.getTarget();
        if (target == null) {
            return;
        }

        UUID parentId = parent.getUniqueId();
        int maximumTotal = Math.max(1, configInt("maximum-active-total", 96));
        int maximumPerParent = Math.max(1, configInt("maximum-active-per-parent", 2));
        if (!reserveSpawn(parentId, maximumTotal, maximumPerParent)) {
            debug("copy spawn skipped parent=" + parentId
                    + " reason=limit activeTotal=" + activeClones.size()
                    + " pendingTotal=" + pendingTotal.get()
                    + " activeForParent=" + activeCount(parentId));
            return;
        }

        Phantom clone = null;
        try {
            Location spawnLocation = cloneSpawnLocation(parent);
            boolean purple = isDragonPhantom(parent);
            clone = parent.getWorld().spawn(spawnLocation, Phantom.class, created -> {
                PersistentDataContainer pdc = created.getPersistentDataContainer();
                pdc.set(cloneKey, PersistentDataType.BYTE, (byte) 1);
                pdc.set(cloneParentKey, PersistentDataType.STRING, parentId.toString());
                if (purple) {
                    pdc.set(clonePurpleKey, PersistentDataType.BYTE, (byte) 1);
                }
            });

            double health = Math.max(1.0D, configDouble("health", 1.0D));
            double attackDamage = Math.max(0.0D, configDouble("attack-damage", 2.0D));
            int size = Math.max(0, configInt("size", 0));
            long lifetimeTicks = Math.max(20L, configLong("lifetime-ticks", 200L));

            clone.setSize(size);
            setBaseAttribute(clone, Attribute.MAX_HEALTH, health);
            setBaseAttribute(clone, Attribute.ATTACK_DAMAGE, attackDamage);
            clone.setHealth(health);
            clone.setShouldBurnInDay(parent.shouldBurnInDay());
            clone.setPersistent(false);
            clone.setRemoveWhenFarAway(true);
            clone.setAware(true);
            clone.setAggressive(true);
            clone.setCanPickupItems(false);
            clone.clearLootTable();
            clone.setCustomNameVisible(false);
            clone.setGlowing(false);
            Location anchor = parent.getAnchorLocation();
            if (anchor != null && anchor.getWorld() == clone.getWorld()) {
                clone.setAnchorLocation(anchor.clone());
            }
            clone.setTarget(target);
            clone.setVelocity(parent.getVelocity().clone().multiply(0.92D));

            CloneState state = new CloneState(clone, parentId, purple);
            registerClone(state);
            state.start(lifetimeTicks);
            spawnCopyParticles(spawnLocation, purple);
            debug("copy spawned parent=" + parentId
                    + " parentDragonPhantom=" + purple
                    + " clone=" + clone.getUniqueId()
                    + " size=" + size
                    + " health=" + health
                    + " attackDamage=" + attackDamage
                    + " lifetimeTicks=" + lifetimeTicks
                    + " activeTotal=" + activeClones.size());
        } catch (Throwable error) {
            if (clone != null && clone.isValid()) {
                clone.remove();
            }
            plugin.getLogger().log(Level.SEVERE,
                    "[phantom-dive-clones] could not create sweep copy parent=" + parentId, error);
        } finally {
            releaseSpawn(parentId);
        }
    }

    private Location cloneSpawnLocation(Phantom parent) {
        Location location = parent.getLocation().clone();
        Vector direction = parent.getVelocity().clone();
        if (direction.lengthSquared() < 0.0001D) {
            direction = location.getDirection();
        }
        direction.setY(0.0D);
        Vector side;
        if (direction.lengthSquared() < 0.0001D) {
            double angle = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
            side = new Vector(Math.cos(angle), 0.0D, Math.sin(angle));
        } else {
            direction.normalize();
            side = new Vector(-direction.getZ(), 0.0D, direction.getX());
            if (ThreadLocalRandom.current().nextBoolean()) {
                side.multiply(-1.0D);
            }
        }
        return location.add(side.multiply(0.75D)).add(0.0D, 0.25D, 0.0D);
    }

    private boolean reserveSpawn(UUID parentId, int maximumTotal, int maximumPerParent) {
        synchronized (spawnLock) {
            int parentPending = pendingByParent.getOrDefault(parentId, 0);
            if (activeClones.size() + pendingTotal.get() >= maximumTotal
                    || activeCount(parentId) + parentPending >= maximumPerParent) {
                return false;
            }
            pendingTotal.incrementAndGet();
            pendingByParent.put(parentId, parentPending + 1);
            return true;
        }
    }

    private void releaseSpawn(UUID parentId) {
        synchronized (spawnLock) {
            pendingTotal.updateAndGet(value -> Math.max(0, value - 1));
            int pending = pendingByParent.getOrDefault(parentId, 0);
            if (pending <= 1) {
                pendingByParent.remove(parentId);
            } else {
                pendingByParent.put(parentId, pending - 1);
            }
        }
    }

    private void registerClone(CloneState state) {
        activeClones.put(state.clone.getUniqueId(), state);
        clonesByParent.computeIfAbsent(state.parentId, ignored -> ConcurrentHashMap.newKeySet())
                .add(state.clone.getUniqueId());
    }

    private void unregisterClone(UUID cloneId, String reason) {
        CloneState state = activeClones.remove(cloneId);
        if (state == null) {
            return;
        }
        state.cancelTasks();
        Set<UUID> ids = clonesByParent.get(state.parentId);
        if (ids != null) {
            ids.remove(cloneId);
            if (ids.isEmpty()) {
                clonesByParent.remove(state.parentId, ids);
            }
        }
        debug("copy unregistered clone=" + cloneId + " parent=" + state.parentId + " reason=" + reason);
    }

    private int activeCount(UUID parentId) {
        Set<UUID> ids = clonesByParent.get(parentId);
        return ids == null ? 0 : ids.size();
    }

    private boolean isDiveClone(Phantom phantom) {
        return phantom.getPersistentDataContainer().has(cloneKey, PersistentDataType.BYTE);
    }

    private boolean isDragonPhantom(Phantom phantom) {
        return phantom.getPersistentDataContainer().has(dragonPhantomKey, PersistentDataType.BYTE);
    }

    private boolean isPurpleClone(Phantom phantom) {
        return phantom.getPersistentDataContainer().has(clonePurpleKey, PersistentDataType.BYTE);
    }

    private void stopTracker(UUID phantomId, String reason) {
        PhantomTracker tracker = trackers.remove(phantomId);
        if (tracker != null) {
            tracker.stop(reason);
        }
    }

    private void spawnCopyParticles(Location location, boolean purple) {
        World world = location.getWorld();
        if (world == null) {
            return;
        }
        Bukkit.getRegionScheduler().execute(plugin, location, () -> {
            world.spawnParticle(Particle.SMOKE, location, 12, 0.45D, 0.25D, 0.45D, 0.02D);
            world.spawnParticle(Particle.REVERSE_PORTAL, location, 10, 0.40D, 0.25D, 0.40D, 0.04D);
            if (purple) {
                world.spawnParticle(Particle.DUST_COLOR_TRANSITION, location,
                        10, 0.45D, 0.25D, 0.45D, 0.01D, PURPLE_AURA);
                world.spawnParticle(Particle.DRAGON_BREATH, location,
                        6, 0.40D, 0.22D, 0.40D, 0.01D);
            }
        });
    }

    private void spawnPurpleAura(Phantom clone) {
        Location location = clone.getLocation().clone();
        World world = location.getWorld();
        if (world == null) {
            return;
        }
        double spread = 0.45D;
        int dust = Math.max(0, configInt("purple-aura.purple-dust-particles", 5));
        int breath = Math.max(0, configInt("purple-aura.dragon-breath-particles", 3));
        int portal = Math.max(0, configInt("purple-aura.portal-particles", 2));
        Bukkit.getRegionScheduler().execute(plugin, location, () -> {
            if (dust > 0) {
                world.spawnParticle(Particle.DUST_COLOR_TRANSITION, location,
                        dust, spread, 0.18D, spread, 0.01D, PURPLE_AURA);
            }
            if (breath > 0) {
                world.spawnParticle(Particle.DRAGON_BREATH, location,
                        breath, spread, 0.18D, spread, 0.01D);
            }
            if (portal > 0) {
                world.spawnParticle(Particle.PORTAL, location,
                        portal, spread, 0.18D, spread, 0.03D);
            }
        });
    }

    private void setBaseAttribute(Phantom phantom, Attribute attribute, double value) {
        AttributeInstance instance = phantom.getAttribute(attribute);
        if (instance != null) {
            instance.setBaseValue(value);
        }
    }

    private int configInt(String key, int fallback) {
        return plugin.getConfig().getInt("phantom-dive-clones." + key, fallback);
    }

    private long configLong(String key, long fallback) {
        return plugin.getConfig().getLong("phantom-dive-clones." + key, fallback);
    }

    private double configDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("phantom-dive-clones." + key, fallback);
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("phantom-dive-clones.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[phantom-dive-clones] " + message);
        }
    }

    private final class PhantomTracker {
        private final Phantom phantom;
        private ScheduledTask task;
        private boolean sweepActive;
        private long cooldownTicks;

        private PhantomTracker(Phantom phantom) {
            this.phantom = phantom;
        }

        private synchronized void start() {
            if (task != null || !running || !phantom.isValid() || phantom.isDead()) {
                return;
            }
            long period = Math.max(1L, configLong("monitor-period-ticks", 1L));
            final ScheduledTask[] holder = new ScheduledTask[1];
            ScheduledTask scheduled = phantom.getScheduler().runAtFixedRate(plugin, current -> {
                holder[0] = current;
                tick(period);
            }, () -> {
                synchronized (PhantomTracker.this) {
                    if (task == holder[0]) {
                        task = null;
                    }
                }
                trackers.remove(phantom.getUniqueId(), PhantomTracker.this);
            }, 1L, period);
            task = scheduled;
            if (scheduled == null) {
                trackers.remove(phantom.getUniqueId(), this);
                debug("tracker scheduler rejected uuid=" + phantom.getUniqueId());
            }
        }

        private void tick(long period) {
            if (!running
                    || !plugin.enabled("phantom-dive-clones")
                    || !phantom.isValid()
                    || phantom.isDead()
                    || isDiveClone(phantom)) {
                stop("invalid-or-disabled");
                return;
            }
            cooldownTicks = Math.max(0L, cooldownTicks - period);
            boolean nowSweeping = isSweepAttackRunning(phantom);
            if (nowSweeping && !sweepActive && cooldownTicks <= 0L) {
                cooldownTicks = Math.max(period, configLong("cooldown-ticks", 40L));
                debug("sweep start detected uuid=" + phantom.getUniqueId()
                        + " dragonPhantom=" + isDragonPhantom(phantom)
                        + " targetPresent=" + (phantom.getTarget() != null)
                        + " action=spawn-one-health-copy");
                spawnDiveClone(phantom);
            }
            sweepActive = nowSweeping;
        }

        private synchronized void stop(String reason) {
            ScheduledTask scheduled = task;
            task = null;
            if (scheduled != null) {
                scheduled.cancel();
            }
            trackers.remove(phantom.getUniqueId(), this);
            debug("tracker stopped uuid=" + phantom.getUniqueId() + " reason=" + reason);
        }
    }

    private final class CloneState {
        private final Phantom clone;
        private final UUID parentId;
        private final boolean purple;
        private ScheduledTask retirementTask;
        private ScheduledTask auraTask;

        private CloneState(Phantom clone, UUID parentId, boolean purple) {
            this.clone = clone;
            this.parentId = parentId;
            this.purple = purple;
        }

        private void start(long lifetimeTicks) {
            retirementTask = clone.getScheduler().runDelayed(plugin, task -> {
                if (clone.isValid()) {
                    clone.remove();
                }
                unregisterClone(clone.getUniqueId(), "lifetime-ended");
            }, () -> unregisterClone(clone.getUniqueId(), "retired"), lifetimeTicks);

            if (purple && isPurpleClone(clone)) {
                long period = Math.max(2L, configLong("purple-aura.period-ticks", 5L));
                auraTask = clone.getScheduler().runAtFixedRate(plugin, task -> {
                    if (!running || !clone.isValid() || clone.isDead()) {
                        task.cancel();
                        return;
                    }
                    if (clone.isGlowing()) {
                        clone.setGlowing(false);
                    }
                    spawnPurpleAura(clone);
                }, () -> unregisterClone(clone.getUniqueId(), "aura-retired"), 1L, period);
            }
        }

        private void cancelTasks() {
            ScheduledTask retirement = retirementTask;
            retirementTask = null;
            if (retirement != null) {
                retirement.cancel();
            }
            ScheduledTask aura = auraTask;
            auraTask = null;
            if (aura != null) {
                aura.cancel();
            }
        }

        private void stopAndRemove(String reason) {
            cancelTasks();
            clone.getScheduler().run(plugin, task -> {
                if (clone.isValid() && isDiveClone(clone)) {
                    clone.remove();
                }
            }, null);
            unregisterClone(clone.getUniqueId(), reason);
        }
    }
}
