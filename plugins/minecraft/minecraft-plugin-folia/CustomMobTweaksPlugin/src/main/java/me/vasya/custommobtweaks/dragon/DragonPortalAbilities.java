package me.vasya.custommobtweaks.dragon;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.Chunk;
import org.bukkit.Color;
import org.bukkit.GameMode;
import org.bukkit.HeightMap;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.block.Block;
import org.bukkit.entity.EnderCrystal;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.Player;
import org.bukkit.persistence.PersistentDataContainer;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
import org.bukkit.util.Vector;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.logging.Level;

/** Post-landing dragon abilities: void roar, crystal field and primary health scaling. */
public final class DragonPortalAbilities {
    private static final Particle.DustTransition VOID_DUST = new Particle.DustTransition(
            Color.fromRGB(47, 0, 82),
            Color.fromRGB(213, 74, 255),
            1.45F
    );

    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey crystalKey = new NamespacedKey("taskforge", "dragon_crystal");
    private final NamespacedKey crystalOwnerKey = new NamespacedKey("taskforge", "dragon_crystal_owner");
    private final Map<UUID, EnderCrystal> activeCrystals = new ConcurrentHashMap<>();
    private final Map<UUID, UUID> crystalOwners = new ConcurrentHashMap<>();
    private final Map<UUID, Set<UUID>> crystalsByOwner = new ConcurrentHashMap<>();
    private final Map<UUID, Long> elytraLocksUntilNanos = new ConcurrentHashMap<>();
    private final Set<RoarState> activeRoars = ConcurrentHashMap.newKeySet();
    private final AtomicInteger startupCrystalTasks = new AtomicInteger();
    private volatile boolean running;

    public DragonPortalAbilities(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    public void start() {
        running = true;
        inspectLoadedCrystals();
        debug("portal abilities started healthMultiplier=" + primaryHealthMultiplier()
                + " voidRoar=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.void-roar.enabled", true)
                + " crystalField=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.crystal-field.enabled", true)
                + " crystalCount=" + crystalInt("count-min", 18)
                + "-" + crystalInt("count-max", 24)
                + " crystalMaximumActive=" + crystalInt("maximum-active", 48));
    }

    public void shutdown() {
        running = false;
        for (RoarState state : Set.copyOf(activeRoars)) {
            finishRoar(state, "shutdown");
        }
        for (EnderCrystal crystal : Set.copyOf(activeCrystals.values())) {
            removeCrystalOnEntityThread(crystal, "shutdown");
        }
        activeCrystals.clear();
        crystalOwners.clear();
        crystalsByOwner.clear();
        elytraLocksUntilNanos.clear();
        debug("portal abilities stopped");
    }

    public void applyPrimaryHealth(EnderDragon dragon, String source) {
        if (!running || !dragon.isValid() || dragon.isDead()) {
            return;
        }
        AttributeInstance instance = dragon.getAttribute(Attribute.MAX_HEALTH);
        if (instance == null) {
            dragon.registerAttribute(Attribute.MAX_HEALTH);
            instance = dragon.getAttribute(Attribute.MAX_HEALTH);
        }
        if (instance == null) {
            plugin.getLogger().warning("[dragon][portal] primary dragon has no MAX_HEALTH uuid="
                    + dragon.getUniqueId());
            return;
        }

        double vanillaMaximum = 200.0D;
        try {
            if (EntityType.ENDER_DRAGON.hasDefaultAttributes()) {
                AttributeInstance defaults = EntityType.ENDER_DRAGON.getDefaultAttributes()
                        .getAttribute(Attribute.MAX_HEALTH);
                if (defaults != null && Double.isFinite(defaults.getBaseValue()) && defaults.getBaseValue() > 0.0D) {
                    vanillaMaximum = defaults.getBaseValue();
                }
            }
        } catch (Throwable error) {
            debug("could not read EnderDragon default health; using fallback=200.0 error="
                    + error.getClass().getSimpleName());
        }

        double multiplier = primaryHealthMultiplier();
        double targetMaximum = Math.max(1.0D, vanillaMaximum * multiplier);
        double oldMaximum = Math.max(1.0D, instance.getValue());
        double oldHealth = Math.max(0.0D, Math.min(dragon.getHealth(), oldMaximum));
        double healthRatio = oldHealth / oldMaximum;

        instance.setBaseValue(targetMaximum);
        double appliedMaximum = Math.max(1.0D, instance.getValue());
        double targetHealth = Math.max(0.01D, Math.min(appliedMaximum, appliedMaximum * healthRatio));
        dragon.setHealth(targetHealth);

        debug("primary health applied source=" + source
                + " uuid=" + dragon.getUniqueId()
                + " vanillaMaximum=" + format(vanillaMaximum)
                + " multiplier=" + format(multiplier)
                + " oldMaximum=" + format(oldMaximum)
                + " newMaximum=" + format(appliedMaximum)
                + " oldHealth=" + format(oldHealth)
                + " newHealth=" + format(targetHealth));
    }

    public void triggerAfterLanding(EnderDragon dragon, Location portal, int landingCycle, String source) {
        if (!running || !dragon.isValid() || dragon.isDead() || portal == null || portal.getWorld() == null) {
            debug("post-landing abilities skipped dragon=" + dragon.getUniqueId()
                    + " cycle=" + landingCycle + " reason=runtime-dragon-or-portal");
            return;
        }
        debug("post-landing abilities trigger dragon=" + dragon.getUniqueId()
                + " cycle=" + landingCycle
                + " source=" + source
                + " portal=" + formatLocation(portal));

        if (plugin.getConfig().getBoolean("ender-dragon-rework.void-roar.enabled", true)) {
            startVoidRoar(dragon, portal, landingCycle);
        }
        if (plugin.getConfig().getBoolean("ender-dragon-rework.crystal-field.enabled", true)) {
            spawnCrystalField(dragon, portal, landingCycle);
        }
    }

    public void handlePrimaryDragonDeath(EnderDragon dragon) {
        UUID ownerId = dragon.getUniqueId();
        for (RoarState state : Set.copyOf(activeRoars)) {
            if (state.dragonId.equals(ownerId)) {
                finishRoar(state, "primary-death");
            }
        }
        removeOwnedCrystals(ownerId, "primary-death");
    }

    public void handleEntityAdded(Entity entity, String source) {
        if (!running || !(entity instanceof EnderCrystal crystal) || !isDragonCrystal(crystal)) {
            return;
        }
        crystal.getScheduler().runDelayed(plugin, task -> {
            if (!running || !crystal.isValid() || !isDragonCrystal(crystal)) {
                return;
            }
            UUID ownerId = ownerOf(crystal);
            if (ownerId == null) {
                debug("marked crystal missing owner source=" + source
                        + " uuid=" + crystal.getUniqueId() + " action=remove");
                crystal.remove();
                return;
            }
            registerCrystal(crystal, ownerId);
            debug("marked crystal restored source=" + source
                    + " uuid=" + crystal.getUniqueId()
                    + " owner=" + ownerId
                    + " location=" + formatLocation(crystal.getLocation()));
        }, () -> unregisterCrystal(crystal.getUniqueId(), crystalOwners.get(crystal.getUniqueId())), 1L);
    }

    public void handleEntitiesLoad(Collection<Entity> entities) {
        if (!running) {
            return;
        }
        for (Entity entity : entities) {
            handleEntityAdded(entity, "entities-load");
        }
    }

    public void handleEntityRemoved(Entity entity) {
        if (entity instanceof EnderCrystal crystal && isDragonCrystal(crystal)) {
            UUID crystalId = crystal.getUniqueId();
            UUID ownerId = crystalOwners.get(crystalId);
            if (ownerId == null) {
                ownerId = ownerOf(crystal);
            }
            unregisterCrystal(crystalId, ownerId);
        }
    }

    public boolean isDragonCrystal(EnderCrystal crystal) {
        return crystal.getPersistentDataContainer().has(crystalKey, PersistentDataType.BYTE);
    }

    public boolean isElytraLocked(Player player) {
        Long deadline = elytraLocksUntilNanos.get(player.getUniqueId());
        if (deadline == null) {
            return false;
        }
        if (System.nanoTime() >= deadline) {
            elytraLocksUntilNanos.remove(player.getUniqueId(), deadline);
            return false;
        }
        return true;
    }

    private void startVoidRoar(EnderDragon dragon, Location portal, int landingCycle) {
        double maximumRadius = Math.max(3.0D, roarDouble("maximum-radius", 20.0D));
        double ringStep = Math.max(0.5D, roarDouble("ring-step", 3.0D));
        long periodTicks = Math.max(1L, roarLong("expansion-period-ticks", 2L));
        RoarState state = new RoarState(dragon.getUniqueId(), landingCycle, portal.clone(),
                maximumRadius, ringStep, periodTicks);
        activeRoars.add(state);

        Bukkit.getRegionScheduler().execute(plugin, portal, () -> {
            World world = portal.getWorld();
            if (running && world != null) {
                world.playSound(portal, Sound.ENTITY_ENDER_DRAGON_GROWL, 8.0F, 0.55F);
                world.playSound(portal, Sound.BLOCK_RESPAWN_ANCHOR_DEPLETE, 5.0F, 0.65F);
            }
        });

        ScheduledTask scheduled = dragon.getScheduler().runAtFixedRate(plugin, task -> {
            state.task = task;
            if (!running || !dragon.isValid() || dragon.isDead()) {
                finishRoar(state, "dragon-retired");
                return;
            }
            double radius = state.nextRadius();
            if (radius > state.maximumRadius + 0.0001D) {
                finishRoar(state, "completed");
                return;
            }
            renderRoarRing(state, radius);
            hitPlayersWithRoar(dragon, state, radius);
        }, () -> finishRoar(state, "entity-retired"), 1L, periodTicks);
        state.task = scheduled;
        if (scheduled == null) {
            finishRoar(state, "scheduler-rejected");
            return;
        }
        debug("void roar started dragon=" + dragon.getUniqueId()
                + " cycle=" + landingCycle
                + " portal=" + formatLocation(portal)
                + " maximumRadius=" + format(maximumRadius)
                + " ringStep=" + format(ringStep)
                + " periodTicks=" + periodTicks);
    }

    private void renderRoarRing(RoarState state, double radius) {
        World world = state.portal.getWorld();
        if (world == null || !state.active.get()) {
            return;
        }
        int points = Math.max(16, roarInt("points-per-ring", 72));
        double lowerY = roarDouble("lower-ring-height", 1.2D);
        double upperY = roarDouble("upper-ring-height", 3.2D);
        int breath = Math.max(0, roarInt("dragon-breath-particles-per-point", 1));
        int portalParticles = Math.max(0, roarInt("portal-particles-per-point", 1));
        int smoke = Math.max(0, roarInt("smoke-particles-per-point", 1));

        for (int index = 0; index < points; index++) {
            double angle = (Math.PI * 2.0D * index) / points;
            double x = state.portal.getX() + Math.cos(angle) * radius;
            double z = state.portal.getZ() + Math.sin(angle) * radius;
            Location lower = new Location(world, x, state.portal.getY() + lowerY, z);
            Location upper = new Location(world, x, state.portal.getY() + upperY, z);
            dispatchRoarPoint(state, lower, breath, portalParticles, smoke, true);
            if ((index & 1) == 0) {
                dispatchRoarPoint(state, upper, Math.max(1, breath), portalParticles, 0, false);
            }
        }
        state.rings.incrementAndGet();
    }

    private void dispatchRoarPoint(RoarState state, Location point, int breath,
                                   int portalParticles, int smoke, boolean dust) {
        Bukkit.getRegionScheduler().execute(plugin, point, () -> {
            if (!running || !state.active.get() || point.getWorld() == null) {
                return;
            }
            World world = point.getWorld();
            if (dust) {
                world.spawnParticle(Particle.DUST_COLOR_TRANSITION, point, 1,
                        0.05D, 0.05D, 0.05D, 0.0D, VOID_DUST);
            }
            if (breath > 0) {
                world.spawnParticle(Particle.DRAGON_BREATH, point, breath,
                        0.08D, 0.08D, 0.08D, 0.01D);
            }
            if (portalParticles > 0) {
                world.spawnParticle(Particle.PORTAL, point, portalParticles,
                        0.08D, 0.08D, 0.08D, 0.03D);
            }
            if (smoke > 0) {
                world.spawnParticle(Particle.SMOKE, point, smoke,
                        0.08D, 0.08D, 0.08D, 0.01D);
            }
        });
    }

    private void hitPlayersWithRoar(EnderDragon dragon, RoarState state, double radius) {
        List<Player> candidates = new ArrayList<>(dragon.getTrackedBy());
        if (candidates.isEmpty()) {
            return;
        }
        double thickness = Math.max(0.5D, roarDouble("ring-thickness", 1.75D));
        double verticalRange = Math.max(2.0D, roarDouble("vertical-range", 12.0D));
        UUID worldId = dragon.getWorld().getUID();

        for (Player player : candidates) {
            player.getScheduler().run(plugin, task -> {
                if (!running
                        || !state.active.get()
                        || !player.isOnline()
                        || player.isDead()
                        || player.getHealth() <= 0.0D
                        || player.getGameMode() == GameMode.SPECTATOR
                        || !player.getWorld().getUID().equals(worldId)
                        || state.hitPlayers.contains(player.getUniqueId())) {
                    return;
                }
                Location location = player.getLocation();
                double dx = location.getX() - state.portal.getX();
                double dz = location.getZ() - state.portal.getZ();
                double horizontal = Math.sqrt(dx * dx + dz * dz);
                double vertical = Math.abs(location.getY() - state.portal.getY());
                if (Math.abs(horizontal - radius) > thickness || vertical > verticalRange) {
                    return;
                }
                if (!state.hitPlayers.add(player.getUniqueId())) {
                    return;
                }
                applyRoarHit(player, state.portal, horizontal, dx, dz);
                state.playersHit.incrementAndGet();
            }, () -> state.hitPlayers.remove(player.getUniqueId()));
        }
    }

    private void applyRoarHit(Player player, Location portal, double horizontal, double dx, double dz) {
        double damage = Math.max(0.0D, roarDouble("damage", 4.0D));
        if (damage > 0.0D) {
            player.damage(damage);
        }

        double horizontalKnockback = Math.max(0.0D, roarDouble("horizontal-knockback", 2.2D));
        double verticalKnockback = roarDouble("vertical-knockback", 0.65D);
        Vector push;
        if (horizontal <= 0.0001D) {
            double angle = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
            push = new Vector(Math.cos(angle), 0.0D, Math.sin(angle));
        } else {
            push = new Vector(dx / horizontal, 0.0D, dz / horizontal);
        }
        Vector velocity = player.getVelocity().multiply(0.25D)
                .add(push.multiply(horizontalKnockback));
        velocity.setY(Math.max(velocity.getY(), verticalKnockback));
        player.setVelocity(velocity);

        int darknessTicks = Math.max(0, roarInt("darkness-duration-ticks", 80));
        if (darknessTicks > 0) {
            player.addPotionEffect(new PotionEffect(PotionEffectType.DARKNESS, darknessTicks, 0, false, true, true));
        }

        int elytraTicks = Math.max(0, roarInt("disable-elytra-duration-ticks", 50));
        if (elytraTicks > 0) {
            if (player.isGliding()) {
                player.setGliding(false);
            }
            long deadline = System.nanoTime() + elytraTicks * 50_000_000L;
            UUID playerId = player.getUniqueId();
            elytraLocksUntilNanos.merge(playerId, deadline, (first, second) -> Math.max(first, second));
            player.getScheduler().runDelayed(plugin, task ->
                            elytraLocksUntilNanos.remove(playerId, deadline),
                    () -> elytraLocksUntilNanos.remove(playerId, deadline),
                    Math.max(1L, elytraTicks + 1L));
        }
        debug("void roar hit player=" + player.getName() + "/" + player.getUniqueId()
                + " portal=" + formatLocation(portal)
                + " damage=" + format(damage)
                + " elytraLockTicks=" + elytraTicks);
    }

    private void finishRoar(RoarState state, String reason) {
        if (!state.active.compareAndSet(true, false)) {
            return;
        }
        ScheduledTask task = state.task;
        if (task != null) {
            task.cancel();
        }
        activeRoars.remove(state);
        debug("void roar finished dragon=" + state.dragonId
                + " cycle=" + state.landingCycle
                + " reason=" + reason
                + " rings=" + state.rings.get()
                + " playersHit=" + state.playersHit.get());
    }

    private void spawnCrystalField(EnderDragon dragon, Location portal, int landingCycle) {
        World world = portal.getWorld();
        if (world == null) {
            return;
        }
        UUID ownerId = dragon.getUniqueId();
        int minimum = Math.max(1, crystalInt("count-min", 18));
        int maximum = Math.max(minimum, crystalInt("count-max", 24));
        int requested = minimum == maximum
                ? minimum
                : ThreadLocalRandom.current().nextInt(minimum, maximum + 1);
        int maximumActive = Math.max(0, crystalInt("maximum-active", 48));
        int activeBefore = activeCrystalCount(ownerId);
        int spawnTarget = Math.min(requested, Math.max(0, maximumActive - activeBefore));
        if (spawnTarget <= 0) {
            debug("crystal field skipped dragon=" + ownerId
                    + " cycle=" + landingCycle
                    + " reason=limit active=" + activeBefore
                    + " maximum=" + maximumActive);
            return;
        }

        int attemptsPerCrystal = Math.max(2, crystalInt("placement-attempts-per-crystal", 8));
        int totalAttempts = spawnTarget * attemptsPerCrystal;
        double minimumRadius = Math.max(6.0D, crystalDouble("minimum-radius", 18.0D));
        double maximumRadius = Math.max(minimumRadius, crystalDouble("maximum-radius", 96.0D));
        CrystalSpawnBatch batch = new CrystalSpawnBatch(ownerId, landingCycle, spawnTarget, totalAttempts);

        debug("crystal field dispatch dragon=" + ownerId
                + " cycle=" + landingCycle
                + " portal=" + formatLocation(portal)
                + " requested=" + requested
                + " target=" + spawnTarget
                + " active=" + activeBefore + "/" + maximumActive
                + " radius=" + format(minimumRadius) + "-" + format(maximumRadius)
                + " attempts=" + totalAttempts);

        ThreadLocalRandom random = ThreadLocalRandom.current();
        for (int attempt = 0; attempt < totalAttempts; attempt++) {
            double angle = random.nextDouble(0.0D, Math.PI * 2.0D);
            double radiusSquared = random.nextDouble(minimumRadius * minimumRadius,
                    maximumRadius * maximumRadius);
            double radius = Math.sqrt(radiusSquared);
            int x = (int) Math.floor(portal.getX() + Math.cos(angle) * radius);
            int z = (int) Math.floor(portal.getZ() + Math.sin(angle) * radius);
            Location probe = new Location(world, x + 0.5D, portal.getY(), z + 0.5D);
            Bukkit.getRegionScheduler().execute(plugin, probe,
                    () -> trySpawnCrystalAt(batch, world, x, z));
        }
    }

    private void trySpawnCrystalAt(CrystalSpawnBatch batch, World world, int x, int z) {
        try {
            if (!running || batch.remaining.get() <= 0) {
                return;
            }
            Block surface = world.getHighestBlockAt(x, z, HeightMap.MOTION_BLOCKING_NO_LEAVES);
            Material type = surface.getType();
            if (type != Material.END_STONE && type != Material.OBSIDIAN) {
                return;
            }
            if (!surface.getRelative(0, 1, 0).isEmpty()
                    || !surface.getRelative(0, 2, 0).isEmpty()) {
                return;
            }
            Location spawnLocation = surface.getLocation().add(0.5D, 1.0D, 0.5D);
            if (!world.getNearbyEntities(spawnLocation, 1.25D, 2.5D, 1.25D,
                    entity -> entity instanceof EnderCrystal).isEmpty()) {
                return;
            }
            if (!reserveCrystalSlot(batch.remaining)) {
                return;
            }

            EnderCrystal crystal;
            try {
                crystal = world.spawn(spawnLocation, EnderCrystal.class, created -> {
                    PersistentDataContainer pdc = created.getPersistentDataContainer();
                    pdc.set(crystalKey, PersistentDataType.BYTE, (byte) 1);
                    pdc.set(crystalOwnerKey, PersistentDataType.STRING, batch.ownerId.toString());
                    created.setShowingBottom(plugin.getConfig().getBoolean(
                            "ender-dragon-rework.crystal-field.show-bottom", true));
                    created.setPersistent(true);
                    created.setInvulnerable(false);
                });
            } catch (Throwable error) {
                batch.remaining.incrementAndGet();
                throw error;
            }
            registerCrystal(crystal, batch.ownerId);
            int spawned = batch.spawned.incrementAndGet();
            debug("dragon crystal spawned uuid=" + crystal.getUniqueId()
                    + " owner=" + batch.ownerId
                    + " cycle=" + batch.landingCycle
                    + " member=" + spawned + "/" + batch.target
                    + " location=" + formatLocation(crystal.getLocation()));
        } catch (Throwable error) {
            plugin.getLogger().log(Level.WARNING,
                    "[dragon][portal] crystal spawn failed owner=" + batch.ownerId
                            + " cycle=" + batch.landingCycle
                            + " x=" + x + " z=" + z, error);
        } finally {
            if (batch.attemptsRemaining.decrementAndGet() == 0) {
                debug("crystal field complete dragon=" + batch.ownerId
                        + " cycle=" + batch.landingCycle
                        + " target=" + batch.target
                        + " spawned=" + batch.spawned.get()
                        + " activeNow=" + activeCrystalCount(batch.ownerId));
            }
        }
    }

    private boolean reserveCrystalSlot(AtomicInteger remaining) {
        while (true) {
            int current = remaining.get();
            if (current <= 0) {
                return false;
            }
            if (remaining.compareAndSet(current, current - 1)) {
                return true;
            }
        }
    }

    private void registerCrystal(EnderCrystal crystal, UUID ownerId) {
        UUID crystalId = crystal.getUniqueId();
        activeCrystals.put(crystalId, crystal);
        crystalOwners.put(crystalId, ownerId);
        crystalsByOwner.computeIfAbsent(ownerId, ignored -> ConcurrentHashMap.newKeySet())
                .add(crystalId);
    }

    private void unregisterCrystal(UUID crystalId, UUID ownerId) {
        activeCrystals.remove(crystalId);
        UUID trackedOwner = crystalOwners.remove(crystalId);
        if (ownerId == null) {
            ownerId = trackedOwner;
        }
        if (ownerId == null) {
            return;
        }
        Set<UUID> ids = crystalsByOwner.get(ownerId);
        if (ids != null) {
            ids.remove(crystalId);
            if (ids.isEmpty()) {
                crystalsByOwner.remove(ownerId, ids);
            }
        }
    }

    private UUID ownerOf(EnderCrystal crystal) {
        String raw = crystal.getPersistentDataContainer().get(crystalOwnerKey, PersistentDataType.STRING);
        if (raw == null) {
            return null;
        }
        try {
            return UUID.fromString(raw);
        } catch (IllegalArgumentException ignored) {
            return null;
        }
    }

    private int activeCrystalCount(UUID ownerId) {
        Set<UUID> ids = crystalsByOwner.get(ownerId);
        return ids == null ? 0 : ids.size();
    }

    private void removeOwnedCrystals(UUID ownerId, String reason) {
        Set<UUID> ids = crystalsByOwner.remove(ownerId);
        if (ids == null || ids.isEmpty()) {
            return;
        }
        for (UUID crystalId : Set.copyOf(ids)) {
            EnderCrystal crystal = activeCrystals.get(crystalId);
            if (crystal != null) {
                removeCrystalOnEntityThread(crystal, reason);
            } else {
                unregisterCrystal(crystalId, ownerId);
            }
        }
    }

    private void removeCrystalOnEntityThread(EnderCrystal crystal, String reason) {
        UUID crystalId = crystal.getUniqueId();
        UUID ownerId = crystalOwners.get(crystalId);
        crystal.getScheduler().run(plugin, task -> {
            unregisterCrystal(crystalId, ownerId);
            if (crystal.isValid() && isDragonCrystal(crystal)) {
                debug("dragon crystal remove uuid=" + crystal.getUniqueId()
                        + " owner=" + ownerId + " reason=" + reason);
                crystal.remove();
            }
        }, () -> unregisterCrystal(crystalId, ownerId));
    }

    private void inspectLoadedCrystals() {
        int chunks = 0;
        for (World world : Bukkit.getWorlds()) {
            for (Chunk chunk : world.getLoadedChunks()) {
                chunks++;
                startupCrystalTasks.incrementAndGet();
                Location anchor = new Location(world,
                        (chunk.getX() << 4) + 8.0D,
                        world.getMinHeight() + 1.0D,
                        (chunk.getZ() << 4) + 8.0D);
                Bukkit.getRegionScheduler().execute(plugin, anchor, () -> {
                    try {
                        if (!running) {
                            return;
                        }
                        for (Entity entity : chunk.getEntities()) {
                            if (entity instanceof EnderCrystal crystal && isDragonCrystal(crystal)) {
                                UUID ownerId = ownerOf(crystal);
                                if (ownerId == null) {
                                    crystal.remove();
                                } else {
                                    registerCrystal(crystal, ownerId);
                                }
                            }
                        }
                    } finally {
                        if (startupCrystalTasks.decrementAndGet() == 0) {
                            debug("startup crystal inspection complete tracked=" + activeCrystals.size());
                        }
                    }
                });
            }
        }
        debug("startup crystal inspection scheduled chunks=" + chunks);
    }

    private double primaryHealthMultiplier() {
        return Math.max(0.05D, plugin.getConfig().getDouble(
                "ender-dragon-rework.primary-dragon.health-multiplier", 2.0D));
    }

    private int roarInt(String key, int fallback) {
        return plugin.getConfig().getInt("ender-dragon-rework.void-roar." + key, fallback);
    }

    private long roarLong(String key, long fallback) {
        return plugin.getConfig().getLong("ender-dragon-rework.void-roar." + key, fallback);
    }

    private double roarDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("ender-dragon-rework.void-roar." + key, fallback);
    }

    private int crystalInt(String key, int fallback) {
        return plugin.getConfig().getInt("ender-dragon-rework.crystal-field." + key, fallback);
    }

    private double crystalDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("ender-dragon-rework.crystal-field." + key, fallback);
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("ender-dragon-rework.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[dragon][portal] " + message);
        }
    }

    private String formatLocation(Location location) {
        if (location == null) {
            return "null";
        }
        return String.format(java.util.Locale.ROOT, "%.2f,%.2f,%.2f",
                location.getX(), location.getY(), location.getZ());
    }

    private String format(double value) {
        return String.format(java.util.Locale.ROOT, "%.2f", value);
    }

    private static final class RoarState {
        private final UUID dragonId;
        private final int landingCycle;
        private final Location portal;
        private final double maximumRadius;
        private final double ringStep;
        private final long periodTicks;
        private final AtomicBoolean active = new AtomicBoolean(true);
        private final AtomicInteger rings = new AtomicInteger();
        private final AtomicInteger playersHit = new AtomicInteger();
        private final Set<UUID> hitPlayers = ConcurrentHashMap.newKeySet();
        private double radius;
        private volatile ScheduledTask task;

        private RoarState(UUID dragonId, int landingCycle, Location portal,
                          double maximumRadius, double ringStep, long periodTicks) {
            this.dragonId = dragonId;
            this.landingCycle = landingCycle;
            this.portal = portal;
            this.maximumRadius = maximumRadius;
            this.ringStep = ringStep;
            this.periodTicks = periodTicks;
        }

        private synchronized double nextRadius() {
            radius += ringStep;
            return radius;
        }
    }

    private static final class CrystalSpawnBatch {
        private final UUID ownerId;
        private final int landingCycle;
        private final int target;
        private final AtomicInteger remaining;
        private final AtomicInteger attemptsRemaining;
        private final AtomicInteger spawned = new AtomicInteger();

        private CrystalSpawnBatch(UUID ownerId, int landingCycle, int target, int attempts) {
            this.ownerId = ownerId;
            this.landingCycle = landingCycle;
            this.target = target;
            this.remaining = new AtomicInteger(target);
            this.attemptsRemaining = new AtomicInteger(attempts);
        }
    }
}
