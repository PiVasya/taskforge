package me.vasya.custommobtweaks.dragon;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.Color;
import org.bukkit.FluidCollisionMode;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.block.BlockFace;
import org.bukkit.entity.AreaEffectCloud;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
import org.bukkit.util.RayTraceResult;
import org.bukkit.util.Vector;

import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.logging.Level;

/** Sustained wide three-dimensional dragon fire stream with real ground fire and breath clouds. */
public final class DragonBreathAttack {
    private static final Particle.DustTransition PURPLE_FLAME = new Particle.DustTransition(
            Color.fromRGB(203, 63, 255),
            Color.fromRGB(91, 0, 150),
            1.2F
    );

    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey cloudKey;
    private final AtomicBoolean running = new AtomicBoolean(false);
    private final AtomicLong sequence = new AtomicLong();
    private final Set<AttackState> activeAttacks = ConcurrentHashMap.newKeySet();
    private final Set<AreaEffectCloud> activeClouds = ConcurrentHashMap.newKeySet();
    private final Map<BlockKey, Location> placedFire = new ConcurrentHashMap<>();

    public DragonBreathAttack(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.cloudKey = new NamespacedKey(plugin, "dragon_fire_stream_cloud");
    }

    public void enable() {
        running.set(true);
        debug("fire stream runtime enabled");
    }

    public void start(EnderDragon dragon, Location projectileOrigin, Vector projectileDirection) {
        if (!running.get() || projectileOrigin.getWorld() == null || projectileDirection.lengthSquared() < 0.0001D) {
            debug("fire stream rejected dragon=" + dragon.getUniqueId()
                    + " running=" + running.get()
                    + " worldPresent=" + (projectileOrigin.getWorld() != null)
                    + " directionLengthSquared=" + projectileDirection.lengthSquared());
            return;
        }

        long attackId = sequence.incrementAndGet();
        long durationTicks = Math.max(20L, configLong("duration-ticks", 140L));
        long periodTicks = Math.max(1L, configLong("period-ticks", 2L));
        Vector direction = projectileDirection.clone().normalize();
        Basis basis = createBasis(direction);
        AttackState state = new AttackState(attackId, dragon.getUniqueId(), direction, basis, durationTicks, periodTicks);
        activeAttacks.add(state);

        playStartSound(projectileOrigin);
        debug("fire stream start id=" + attackId
                + " dragon=" + dragon.getUniqueId()
                + " world=" + projectileOrigin.getWorld().getKey()
                + " origin=" + formatLocation(projectileOrigin)
                + " direction=" + formatVector(direction)
                + " durationTicks=" + durationTicks
                + " periodTicks=" + periodTicks
                + " length=" + configDouble("length", 42.0D)
                + " maxRadius=" + configDouble("maximum-radius", 5.5D));

        final ScheduledTask[] holder = new ScheduledTask[1];
        ScheduledTask task = dragon.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            holder[0] = scheduledTask;
            state.task = scheduledTask;
            if (!running.get()) {
                finish(state, "runtime-stopped");
                return;
            }
            if (!dragon.isValid() || dragon.isDead()) {
                finish(state, "dragon-retired");
                return;
            }
            if (!plugin.enabled("ender-dragon-rework")
                    || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)) {
                finish(state, "disabled-by-config");
                return;
            }

            int elapsed = state.elapsedTicks.getAndAdd((int) periodTicks);
            if (elapsed >= durationTicks) {
                finish(state, "completed");
                return;
            }

            try {
                Location mouth = dragon.getEyeLocation().clone().add(direction.clone().multiply(1.35D));
                renderPulse(state, mouth, elapsed);
            } catch (Throwable error) {
                plugin.getLogger().log(Level.SEVERE,
                        "[dragon][fire] attack failed id=" + attackId + " dragon=" + dragon.getUniqueId(), error);
                finish(state, "exception-" + error.getClass().getSimpleName());
            }
        }, () -> finish(state, "entity-retired"), 1L, periodTicks);

        holder[0] = task;
        state.task = task;
        if (task == null) {
            finish(state, "scheduler-rejected");
        }
    }

    public boolean isDragonBreathCloud(AreaEffectCloud cloud) {
        return cloud.getPersistentDataContainer().has(cloudKey, PersistentDataType.BYTE);
    }

    public void shutdown() {
        running.set(false);
        for (AttackState state : Set.copyOf(activeAttacks)) {
            finish(state, "shutdown");
        }
        for (AreaEffectCloud cloud : Set.copyOf(activeClouds)) {
            cloud.getScheduler().run(plugin, task -> {
                if (cloud.isValid()) {
                    cloud.remove();
                }
                activeClouds.remove(cloud);
            }, () -> activeClouds.remove(cloud));
        }
        removePlacedFire();
        debug("fire stream runtime disabled activeClouds=" + activeClouds.size()
                + " trackedFireBlocks=" + placedFire.size());
    }

    private void renderPulse(AttackState state, Location mouth, int elapsedTicks) {
        World world = mouth.getWorld();
        if (world == null || !state.active.get()) {
            return;
        }

        double fullLength = Math.max(8.0D, configDouble("length", 42.0D));
        long rampTicks = Math.max(1L, configLong("ramp-up-ticks", 18L));
        double reach = fullLength * Math.min(1.0D, (elapsedTicks + state.periodTicks) / (double) rampTicks);
        int slices = Math.max(4, configInt("slices-per-pulse", 10));
        int rays = Math.max(1, configInt("rays-per-slice", 3));
        double spreadRadians = Math.toRadians(Math.max(0.0D, configDouble("spread-angle-degrees", 10.0D)));

        for (int slice = 1; slice <= slices; slice++) {
            double distance = reach * slice / slices;
            double progress = Math.min(1.0D, distance / fullLength);
            double radius = radiusAt(progress);
            for (int ray = 0; ray < rays; ray++) {
                Vector rayDirection = rayDirection(state.basis, ray, rays, progress, spreadRadians, elapsedTicks);
                Location center = mouth.clone().add(rayDirection.multiply(distance));
                double pointRadius = ray == 0 ? radius : Math.max(0.45D, radius * 0.62D);
                dispatchStreamPoint(state, center, pointRadius, elapsedTicks);
            }
        }

        state.pulses.incrementAndGet();
        maybePlayLoopSound(mouth, elapsedTicks);
        maybeBurnGround(state, mouth, reach, elapsedTicks);
    }

    private void dispatchStreamPoint(AttackState state, Location center, double radius, int elapsedTicks) {
        if (center.getWorld() == null) {
            return;
        }
        Bukkit.getRegionScheduler().execute(plugin, center, () -> {
            if (!running.get() || !state.active.get() || center.getWorld() == null) {
                return;
            }
            renderPoint(center, state.basis, radius);
            damagePlayers(state, center, radius, elapsedTicks);
        });
    }

    private void renderPoint(Location center, Basis basis, double radius) {
        World world = center.getWorld();
        if (world == null) {
            return;
        }

        int orange = Math.max(0, configInt("orange-flame-particles-per-point", 2));
        int purple = Math.max(0, configInt("purple-flame-particles-per-point", 1));
        int breath = Math.max(0, configInt("dragon-breath-particles-per-point", 2));
        int smoke = Math.max(0, configInt("smoke-particles-per-point", 1));
        double verticalMultiplier = Math.max(0.1D, configDouble("vertical-radius-multiplier", 0.80D));

        for (int i = 0; i < orange; i++) {
            Location point = randomPoint(center, basis, radius * 0.82D, verticalMultiplier, 0.0D, 1.0D);
            world.spawnParticle(Particle.FLAME, point, 1, 0.04D, 0.04D, 0.04D, 0.025D);
            if ((i & 1) == 0) {
                world.spawnParticle(Particle.SMALL_FLAME, point, 1, 0.02D, 0.02D, 0.02D, 0.01D);
            }
        }
        for (int i = 0; i < purple; i++) {
            Location point = randomPoint(center, basis, radius * 0.92D, verticalMultiplier, 0.0D, 1.0D);
            world.spawnParticle(Particle.DUST_COLOR_TRANSITION, point, 1,
                    0.0D, 0.0D, 0.0D, 0.0D, PURPLE_FLAME);
            world.spawnParticle(Particle.WITCH, point, 1, 0.05D, 0.05D, 0.05D, 0.012D);
        }
        for (int i = 0; i < breath; i++) {
            Location point = randomPoint(center, basis, radius, verticalMultiplier, 0.0D, 1.0D);
            world.spawnParticle(Particle.DRAGON_BREATH, point, 1,
                    0.04D, 0.04D, 0.04D, 0.015D, 0.9F);
        }
        for (int i = 0; i < smoke; i++) {
            Location point = randomPoint(center, basis, radius * 1.08D, verticalMultiplier, 0.70D, 1.15D);
            world.spawnParticle(Particle.SMOKE, point, 1, 0.05D, 0.05D, 0.05D, 0.015D);
        }
    }

    private void damagePlayers(AttackState state, Location center, double radius, int elapsedTicks) {
        World world = center.getWorld();
        if (world == null) {
            return;
        }
        double hitRadius = Math.max(0.85D, radius * 0.95D);
        int damageInterval = Math.max(1, configInt("damage-interval-ticks", 10));
        int reservedUntil = elapsedTicks + damageInterval;

        for (Entity nearby : world.getNearbyEntities(center, hitRadius, hitRadius, hitRadius,
                entity -> entity instanceof Player)) {
            if (!(nearby instanceof Player player)) {
                continue;
            }
            AtomicBoolean reserved = new AtomicBoolean(false);
            state.nextDamageTick.compute(player.getUniqueId(), (uuid, current) -> {
                if (current == null || elapsedTicks >= current) {
                    reserved.set(true);
                    return reservedUntil;
                }
                return current;
            });
            if (!reserved.get()) {
                continue;
            }

            player.getScheduler().run(plugin, task -> {
                if (!running.get()
                        || !state.active.get()
                        || !player.isOnline()
                        || player.isDead()
                        || player.getGameMode() == GameMode.SPECTATOR
                        || player.getGameMode() == GameMode.CREATIVE
                        || player.getWorld() != world
                        || player.getLocation().distanceSquared(center) > hitRadius * hitRadius) {
                    state.nextDamageTick.remove(player.getUniqueId(), reservedUntil);
                    return;
                }

                double damage = Math.max(0.0D, configDouble("damage", 3.0D));
                if (damage > 0.0D) {
                    player.damage(damage);
                    state.damageApplications.incrementAndGet();
                }
                int fireTicks = Math.max(0, configInt("fire-ticks", 100));
                if (fireTicks > player.getFireTicks()) {
                    player.setFireTicks(fireTicks);
                }
            }, () -> state.nextDamageTick.remove(player.getUniqueId(), reservedUntil));
        }
    }

    private void maybeBurnGround(AttackState state, Location mouth, double reach, int elapsedTicks) {
        if (!plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.ground.enabled", true)) {
            return;
        }
        int sampleInterval = Math.max(1, groundInt("sample-interval-ticks", 4));
        if (elapsedTicks % sampleInterval != 0) {
            return;
        }

        int patches = Math.max(1, groundInt("patches-per-sample", 4));
        double minimumDistance = Math.max(1.0D, groundDouble("minimum-distance", 6.0D));
        if (reach <= minimumDistance) {
            return;
        }
        boolean cloudPulse = plugin.getConfig().getBoolean(
                "ender-dragon-rework.fire-stream.ground.create-dragon-breath-clouds", true)
                && elapsedTicks % Math.max(1, groundInt("cloud-interval-ticks", 20)) == 0;

        ThreadLocalRandom random = ThreadLocalRandom.current();
        for (int i = 0; i < patches; i++) {
            double distance = random.nextDouble(minimumDistance, reach + 0.0001D);
            double progress = distance / Math.max(1.0D, configDouble("length", 42.0D));
            double width = radiusAt(Math.min(1.0D, progress))
                    * Math.max(0.0D, groundDouble("lateral-spread-multiplier", 1.25D));
            double lateral = random.nextDouble(-width, width);
            Location airPoint = mouth.clone()
                    .add(state.direction.clone().multiply(distance))
                    .add(state.basis.right.clone().multiply(lateral));
            dispatchGroundPatch(state, airPoint, cloudPulse && i == 0);
        }
    }

    private void dispatchGroundPatch(AttackState state, Location airPoint, boolean createCloud) {
        if (airPoint.getWorld() == null) {
            return;
        }
        Bukkit.getRegionScheduler().execute(plugin, airPoint, () -> {
            if (!running.get() || !state.active.get() || airPoint.getWorld() == null) {
                return;
            }
            Location ground = findGround(airPoint);
            if (ground == null) {
                return;
            }
            if (plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.ground.place-fire", true)) {
                placeTemporaryFire(ground, state);
            }
            World world = ground.getWorld();
            if (world != null) {
                world.spawnParticle(Particle.DRAGON_BREATH, ground.clone().add(0.0D, 0.25D, 0.0D),
                        12, 1.35D, 0.35D, 1.35D, 0.025D, 0.9F);
                world.spawnParticle(Particle.FLAME, ground.clone().add(0.0D, 0.25D, 0.0D),
                        8, 1.15D, 0.25D, 1.15D, 0.02D);
            }
            if (createCloud) {
                createBreathCloud(ground, state);
            }
        });
    }

    private Location findGround(Location airPoint) {
        World world = airPoint.getWorld();
        if (world == null) {
            return null;
        }
        double searchDepth = Math.max(8.0D, groundDouble("search-depth", 192.0D));
        Location start = airPoint.clone().add(0.0D, Math.min(8.0D, searchDepth * 0.15D), 0.0D);
        RayTraceResult result = world.rayTraceBlocks(start, new Vector(0.0D, -1.0D, 0.0D),
                searchDepth, FluidCollisionMode.NEVER, true);
        if (result == null || result.getHitBlock() == null) {
            return null;
        }
        Block target = result.getHitBlock().getRelative(BlockFace.UP);
        return target.getLocation().add(0.5D, 0.0D, 0.5D);
    }

    private void placeTemporaryFire(Location location, AttackState state) {
        Block block = location.getBlock();
        if (!block.getType().isAir() || !block.getRelative(BlockFace.DOWN).getType().isSolid()) {
            return;
        }
        BlockKey key = new BlockKey(location.getWorld().getUID(), block.getX(), block.getY(), block.getZ());
        if (placedFire.putIfAbsent(key, block.getLocation()) != null) {
            return;
        }

        block.setType(Material.FIRE, true);
        state.fireBlocks.incrementAndGet();
        long lifetime = Math.max(20L, groundLong("fire-lifetime-ticks", 180L));
        Bukkit.getRegionScheduler().runDelayed(plugin, block.getLocation(), task -> {
            try {
                Block current = block.getWorld().getBlockAt(block.getX(), block.getY(), block.getZ());
                if (current.getType() == Material.FIRE) {
                    current.setType(Material.AIR, false);
                }
            } finally {
                placedFire.remove(key);
            }
        }, lifetime);
    }

    private void createBreathCloud(Location ground, AttackState state) {
        World world = ground.getWorld();
        if (world == null) {
            return;
        }
        try {
            AreaEffectCloud cloud = world.spawn(ground.clone().add(0.0D, 0.15D, 0.0D), AreaEffectCloud.class, spawned -> {
                spawned.getPersistentDataContainer().set(cloudKey, PersistentDataType.BYTE, (byte) 1);
                spawned.setParticle(Particle.DRAGON_BREATH, 0.9F);
                spawned.setRadius((float) Math.max(0.5D, groundDouble("cloud-radius", 3.5D)));
                spawned.setDuration(Math.max(20, groundInt("cloud-duration-ticks", 100)));
                spawned.setWaitTime(Math.max(0, groundInt("cloud-wait-ticks", 0)));
                spawned.setReapplicationDelay(Math.max(1, groundInt("cloud-reapplication-delay-ticks", 10)));
                spawned.setRadiusOnUse(0.0F);
                spawned.setRadiusPerTick(0.0F);
                spawned.setDurationOnUse(0);
                int amplifier = Math.max(0, groundInt("cloud-instant-damage-amplifier", 0));
                spawned.addCustomEffect(new PotionEffect(PotionEffectType.INSTANT_DAMAGE, 1, amplifier), true);
            });
            activeClouds.add(cloud);
            state.breathClouds.incrementAndGet();
            int duration = Math.max(20, groundInt("cloud-duration-ticks", 100));
            cloud.getScheduler().runDelayed(plugin, task -> {
                if (cloud.isValid()) {
                    cloud.remove();
                }
                activeClouds.remove(cloud);
            }, () -> activeClouds.remove(cloud), duration + 5L);
        } catch (Throwable error) {
            plugin.getLogger().log(Level.WARNING,
                    "[dragon][fire] could not create dragon-breath cloud attackId=" + state.id
                            + " location=" + formatLocation(ground), error);
        }
    }

    private void removePlacedFire() {
        for (Map.Entry<BlockKey, Location> entry : Map.copyOf(placedFire).entrySet()) {
            BlockKey key = entry.getKey();
            Location location = entry.getValue();
            Bukkit.getRegionScheduler().execute(plugin, location, () -> {
                Block block = location.getBlock();
                if (block.getType() == Material.FIRE) {
                    block.setType(Material.AIR, false);
                }
                placedFire.remove(key);
            });
        }
    }

    private void finish(AttackState state, String reason) {
        if (!state.active.compareAndSet(true, false)) {
            return;
        }
        ScheduledTask task = state.task;
        if (task != null) {
            task.cancel();
        }
        activeAttacks.remove(state);
        debug("fire stream finish id=" + state.id
                + " dragon=" + state.dragonId
                + " reason=" + reason
                + " elapsedTicks=" + state.elapsedTicks.get()
                + " pulses=" + state.pulses.get()
                + " damageApplications=" + state.damageApplications.get()
                + " fireBlocks=" + state.fireBlocks.get()
                + " breathClouds=" + state.breathClouds.get());
    }

    private void playStartSound(Location origin) {
        Bukkit.getRegionScheduler().execute(plugin, origin, () -> {
            World world = origin.getWorld();
            if (running.get() && world != null) {
                world.playSound(origin, Sound.ENTITY_ENDER_DRAGON_SHOOT, 3.0F, 0.62F);
                world.playSound(origin, Sound.ITEM_FIRECHARGE_USE, 2.4F, 0.60F);
            }
        });
    }

    private void maybePlayLoopSound(Location mouth, int elapsedTicks) {
        if (elapsedTicks % 20 != 0) {
            return;
        }
        Bukkit.getRegionScheduler().execute(plugin, mouth, () -> {
            World world = mouth.getWorld();
            if (running.get() && world != null) {
                world.playSound(mouth, Sound.BLOCK_FIRE_AMBIENT, 2.2F, 0.70F);
                world.playSound(mouth, Sound.ENTITY_BLAZE_SHOOT, 1.5F, 0.55F);
            }
        });
    }

    private double radiusAt(double progress) {
        double minimum = Math.max(0.2D, configDouble("minimum-radius", 0.75D));
        double maximum = Math.max(minimum, configDouble("maximum-radius", 5.5D));
        double body = Math.pow(Math.max(0.0D, Math.sin(Math.PI * progress)), 0.58D);
        return minimum + (maximum - minimum) * body;
    }

    private Vector rayDirection(Basis basis, int ray, int rays, double progress,
                                double spreadRadians, int elapsedTicks) {
        if (ray == 0 || rays == 1 || spreadRadians <= 0.0001D) {
            return basis.forward.clone();
        }
        int ringCount = rays - 1;
        double angle = (Math.PI * 2.0D * (ray - 1) / ringCount) + elapsedTicks * 0.08D;
        double cone = Math.tan(spreadRadians) * (0.30D + 0.70D * progress);
        return basis.forward.clone()
                .add(basis.right.clone().multiply(Math.cos(angle) * cone))
                .add(basis.up.clone().multiply(Math.sin(angle) * cone * 0.85D))
                .normalize();
    }

    private Location randomPoint(Location center, Basis basis, double radius,
                                 double verticalMultiplier, double minimumNormalizedRadius,
                                 double maximumNormalizedRadius) {
        ThreadLocalRandom random = ThreadLocalRandom.current();
        double normalizedRadius = minimumNormalizedRadius
                + (maximumNormalizedRadius - minimumNormalizedRadius) * Math.sqrt(random.nextDouble());
        double angle = random.nextDouble(0.0D, Math.PI * 2.0D);
        double horizontal = Math.cos(angle) * normalizedRadius * radius;
        double vertical = Math.sin(angle) * normalizedRadius * radius * verticalMultiplier;
        double longitudinalJitter = random.nextDouble(-0.35D, 0.35D);

        return center.clone()
                .add(basis.right.clone().multiply(horizontal))
                .add(basis.up.clone().multiply(vertical))
                .add(basis.forward.clone().multiply(longitudinalJitter));
    }

    private Basis createBasis(Vector forward) {
        Vector reference = Math.abs(forward.getY()) < 0.92D
                ? new Vector(0.0D, 1.0D, 0.0D)
                : new Vector(1.0D, 0.0D, 0.0D);
        Vector right = forward.clone().crossProduct(reference).normalize();
        Vector up = right.clone().crossProduct(forward).normalize();
        return new Basis(forward.clone(), right, up);
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("ender-dragon-rework.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[dragon][fire] " + message);
        }
    }

    private int configInt(String key, int fallback) {
        return plugin.getConfig().getInt("ender-dragon-rework.fire-stream." + key, fallback);
    }

    private long configLong(String key, long fallback) {
        return plugin.getConfig().getLong("ender-dragon-rework.fire-stream." + key, fallback);
    }

    private double configDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("ender-dragon-rework.fire-stream." + key, fallback);
    }

    private int groundInt(String key, int fallback) {
        return plugin.getConfig().getInt("ender-dragon-rework.fire-stream.ground." + key, fallback);
    }

    private long groundLong(String key, long fallback) {
        return plugin.getConfig().getLong("ender-dragon-rework.fire-stream.ground." + key, fallback);
    }

    private double groundDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("ender-dragon-rework.fire-stream.ground." + key, fallback);
    }

    private String formatLocation(Location location) {
        return String.format(java.util.Locale.ROOT, "%.2f,%.2f,%.2f",
                location.getX(), location.getY(), location.getZ());
    }

    private String formatVector(Vector vector) {
        return String.format(java.util.Locale.ROOT, "%.3f,%.3f,%.3f",
                vector.getX(), vector.getY(), vector.getZ());
    }

    private static final class AttackState {
        private final long id;
        private final UUID dragonId;
        private final Vector direction;
        private final Basis basis;
        private final long durationTicks;
        private final long periodTicks;
        private final AtomicBoolean active = new AtomicBoolean(true);
        private final AtomicInteger elapsedTicks = new AtomicInteger();
        private final AtomicInteger pulses = new AtomicInteger();
        private final AtomicInteger damageApplications = new AtomicInteger();
        private final AtomicInteger fireBlocks = new AtomicInteger();
        private final AtomicInteger breathClouds = new AtomicInteger();
        private final Map<UUID, Integer> nextDamageTick = new ConcurrentHashMap<>();
        private volatile ScheduledTask task;

        private AttackState(long id, UUID dragonId, Vector direction, Basis basis,
                            long durationTicks, long periodTicks) {
            this.id = id;
            this.dragonId = dragonId;
            this.direction = direction;
            this.basis = basis;
            this.durationTicks = durationTicks;
            this.periodTicks = periodTicks;
        }
    }

    private record Basis(Vector forward, Vector right, Vector up) {
    }

    private record BlockKey(UUID worldId, int x, int y, int z) {
    }
}
