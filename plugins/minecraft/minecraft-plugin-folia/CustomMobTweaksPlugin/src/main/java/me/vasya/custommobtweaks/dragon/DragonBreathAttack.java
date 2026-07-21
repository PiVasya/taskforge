package me.vasya.custommobtweaks.dragon;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.Color;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.World;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.util.Vector;

import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/** Replaces one real dragon fireball with one moving, three-dimensional ellipsoid fire stream. */
public final class DragonBreathAttack {
    private static final Particle.DustTransition PURPLE_FLAME = new Particle.DustTransition(
            Color.fromRGB(190, 45, 255),
            Color.fromRGB(88, 0, 145),
            1.15F
    );

    private final CustomMobTweaksPlugin plugin;
    private final Set<ScheduledTask> activeTasks = ConcurrentHashMap.newKeySet();
    private final AtomicBoolean running = new AtomicBoolean(false);

    public DragonBreathAttack(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    public void enable() {
        running.set(true);
    }

    public void start(EnderDragon dragon, Location origin, Vector direction) {
        if (!running.get() || origin.getWorld() == null || direction.lengthSquared() < 0.0001D) {
            return;
        }

        double length = Math.max(4.0D, configDouble("length", 48.0D));
        double speed = Math.max(0.25D, configDouble("speed-blocks-per-tick", 3.0D));
        int slicesPerTick = Math.max(1, configInt("slices-per-tick", 2));
        int maximumTicks = Math.max(1, (int) Math.ceil(length / speed));
        Vector normalized = direction.clone().normalize();
        Basis basis = createBasis(normalized);
        Set<UUID> damagedPlayers = ConcurrentHashMap.newKeySet();
        AtomicInteger tickCounter = new AtomicInteger();

        playStartSound(origin);

        final ScheduledTask[] holder = new ScheduledTask[1];
        ScheduledTask task = dragon.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            holder[0] = scheduledTask;
            if (!running.get()
                    || !dragon.isValid()
                    || dragon.isDead()
                    || !plugin.enabled("ender-dragon-rework")
                    || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)) {
                finish(scheduledTask);
                return;
            }

            int tick = tickCounter.getAndIncrement();
            if (tick >= maximumTicks) {
                finish(scheduledTask);
                return;
            }

            double from = tick * speed;
            double step = speed / slicesPerTick;
            for (int slice = 0; slice < slicesPerTick; slice++) {
                double distance = Math.min(length, from + (slice + 1) * step);
                Location center = origin.clone().add(normalized.clone().multiply(distance));
                double progress = Math.min(1.0D, distance / length);
                double radius = radiusAt(progress);
                dispatchSlice(center, basis, radius, damagedPlayers);
            }
        }, () -> {
            ScheduledTask scheduledTask = holder[0];
            if (scheduledTask != null) {
                activeTasks.remove(scheduledTask);
            }
        }, 1L, 1L);

        if (task != null) {
            holder[0] = task;
            activeTasks.add(task);
        }
    }

    public void shutdown() {
        running.set(false);
        for (ScheduledTask task : Set.copyOf(activeTasks)) {
            task.cancel();
        }
        activeTasks.clear();
    }

    private void finish(ScheduledTask task) {
        task.cancel();
        activeTasks.remove(task);
    }

    private void dispatchSlice(
            Location center,
            Basis basis,
            double radius,
            Set<UUID> damagedPlayers
    ) {
        if (center.getWorld() == null) {
            return;
        }
        Bukkit.getRegionScheduler().execute(plugin, center, () -> {
            if (!running.get() || !plugin.enabled("ender-dragon-rework") || center.getWorld() == null) {
                return;
            }
            renderSlice(center, basis, radius);
            damagePlayers(center, radius, damagedPlayers);
        });
    }

    private void renderSlice(Location center, Basis basis, double radius) {
        World world = center.getWorld();
        if (world == null) {
            return;
        }

        int orange = Math.max(0, configInt("orange-flame-particles-per-slice", 8));
        int purple = Math.max(0, configInt("purple-flame-particles-per-slice", 5));
        int breath = Math.max(0, configInt("dragon-breath-particles-per-slice", 5));
        int smoke = Math.max(0, configInt("smoke-particles-per-slice", 2));
        double verticalMultiplier = Math.max(0.1D, configDouble("vertical-radius-multiplier", 0.75D));

        for (int i = 0; i < orange; i++) {
            Location point = randomPoint(center, basis, radius * 0.78D, verticalMultiplier, 0.0D, 1.0D);
            world.spawnParticle(Particle.FLAME, point, 1, 0.02D, 0.02D, 0.02D, 0.015D);
            if ((i & 3) == 0) {
                world.spawnParticle(Particle.SMALL_FLAME, point, 1, 0.01D, 0.01D, 0.01D, 0.005D);
            }
        }
        for (int i = 0; i < purple; i++) {
            Location point = randomPoint(center, basis, radius * 0.88D, verticalMultiplier, 0.05D, 1.0D);
            world.spawnParticle(Particle.DUST_COLOR_TRANSITION, point, 1, 0.0D, 0.0D, 0.0D, 0.0D, PURPLE_FLAME);
            world.spawnParticle(Particle.WITCH, point, 1, 0.025D, 0.025D, 0.025D, 0.01D);
        }
        for (int i = 0; i < breath; i++) {
            Location point = randomPoint(center, basis, radius * 0.92D, verticalMultiplier, 0.0D, 1.0D);
            world.spawnParticle(Particle.DRAGON_BREATH, point, 1, 0.02D, 0.02D, 0.02D, 0.01D, 0.85F);
        }
        for (int i = 0; i < smoke; i++) {
            Location point = randomPoint(center, basis, radius, verticalMultiplier, 0.82D, 1.12D);
            world.spawnParticle(Particle.SMOKE, point, 1, 0.035D, 0.035D, 0.035D, 0.01D);
        }
    }

    private void damagePlayers(
            Location center,
            double radius,
            Set<UUID> damagedPlayers
    ) {
        World world = center.getWorld();
        if (world == null) {
            return;
        }
        double hitRadius = Math.max(0.5D, radius * 0.92D);
        for (Entity nearby : world.getNearbyEntities(center, hitRadius, hitRadius, hitRadius,
                entity -> entity instanceof Player)) {
            if (!(nearby instanceof Player player) || !damagedPlayers.add(player.getUniqueId())) {
                continue;
            }
            player.getScheduler().run(plugin, task -> {
                if (!running.get()
                        || !player.isOnline()
                        || player.isDead()
                        || player.getGameMode() == GameMode.SPECTATOR
                        || player.getGameMode() == GameMode.CREATIVE
                        || player.getWorld() != world
                        || player.getLocation().distanceSquared(center) > hitRadius * hitRadius) {
                    damagedPlayers.remove(player.getUniqueId());
                    return;
                }

                double damage = Math.max(0.0D, configDouble("damage", 8.0D));
                if (damage > 0.0D) {
                    player.damage(damage);
                }
                int fireTicks = Math.max(0, configInt("fire-ticks", 80));
                if (fireTicks > player.getFireTicks()) {
                    player.setFireTicks(fireTicks);
                }
            }, () -> damagedPlayers.remove(player.getUniqueId()));
        }
    }

    private void playStartSound(Location origin) {
        Bukkit.getRegionScheduler().execute(plugin, origin, () -> {
            World world = origin.getWorld();
            if (running.get() && world != null) {
                world.playSound(origin, Sound.ENTITY_ENDER_DRAGON_SHOOT, 2.5F, 0.72F);
            }
        });
    }

    private double radiusAt(double progress) {
        double minimum = Math.max(0.1D, configDouble("minimum-radius", 0.45D));
        double maximum = Math.max(minimum, configDouble("maximum-radius", 3.2D));
        double body = Math.pow(Math.max(0.0D, Math.sin(Math.PI * progress)), 0.72D);
        return minimum + (maximum - minimum) * body;
    }

    private Location randomPoint(
            Location center,
            Basis basis,
            double radius,
            double verticalMultiplier,
            double minimumNormalizedRadius,
            double maximumNormalizedRadius
    ) {
        ThreadLocalRandom random = ThreadLocalRandom.current();
        double normalizedRadius = minimumNormalizedRadius
                + (maximumNormalizedRadius - minimumNormalizedRadius) * Math.sqrt(random.nextDouble());
        double angle = random.nextDouble(0.0D, Math.PI * 2.0D);
        double horizontal = Math.cos(angle) * normalizedRadius * radius;
        double vertical = Math.sin(angle) * normalizedRadius * radius * verticalMultiplier;
        double longitudinalJitter = random.nextDouble(-0.20D, 0.20D);

        return center.clone()
                .add(basis.right().clone().multiply(horizontal))
                .add(basis.up().clone().multiply(vertical))
                .add(basis.forward().clone().multiply(longitudinalJitter));
    }

    private Basis createBasis(Vector forward) {
        Vector reference = Math.abs(forward.getY()) < 0.92D
                ? new Vector(0.0D, 1.0D, 0.0D)
                : new Vector(1.0D, 0.0D, 0.0D);
        Vector right = forward.clone().crossProduct(reference).normalize();
        Vector up = right.clone().crossProduct(forward).normalize();
        return new Basis(forward.clone(), right, up);
    }

    private int configInt(String key, int fallback) {
        return plugin.getConfig().getInt("ender-dragon-rework.fire-stream." + key, fallback);
    }

    private double configDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("ender-dragon-rework.fire-stream." + key, fallback);
    }

    private record Basis(Vector forward, Vector right, Vector up) {
    }

}
