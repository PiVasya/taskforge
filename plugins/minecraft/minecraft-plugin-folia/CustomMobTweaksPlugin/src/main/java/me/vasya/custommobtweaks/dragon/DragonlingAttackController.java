package me.vasya.custommobtweaks.dragon;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Player;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/** Short per-entity controller: circle, acquire a nearby live player, charge, retreat, repeat. */
public final class DragonlingAttackController {
    private final CustomMobTweaksPlugin plugin;
    private final EnderDragon dragon;
    private final AtomicBoolean running = new AtomicBoolean(false);
    private final AtomicBoolean targetSelectionPending = new AtomicBoolean(false);
    private volatile ScheduledTask task;
    private long cooldownTicks;
    private long chargeTicksRemaining;
    private boolean charging;

    public DragonlingAttackController(CustomMobTweaksPlugin plugin, EnderDragon dragon) {
        this.plugin = plugin;
        this.dragon = dragon;
        this.cooldownTicks = randomCooldown();
    }

    public EnderDragon dragon() {
        return dragon;
    }

    public synchronized void start() {
        if (task != null || !dragon.isValid() || dragon.isDead()) {
            return;
        }
        running.set(true);
        long period = Math.max(2L, plugin.getConfig().getLong(
                "ender-dragon-rework.dragonling.controller-period-ticks", 5L));
        final ScheduledTask[] holder = new ScheduledTask[1];
        ScheduledTask scheduled = dragon.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            holder[0] = scheduledTask;
            tick(period);
        }, () -> {
            running.set(false);
            targetSelectionPending.set(false);
            synchronized (DragonlingAttackController.this) {
                if (task == holder[0]) {
                    task = null;
                }
            }
        }, 1L, period);
        task = scheduled;
        if (scheduled == null) {
            running.set(false);
            debug("controller scheduler rejected uuid=" + dragon.getUniqueId());
        } else {
            debug("controller started uuid=" + dragon.getUniqueId()
                    + " periodTicks=" + period
                    + " firstCooldownTicks=" + cooldownTicks);
        }
    }

    public synchronized void stop() {
        running.set(false);
        ScheduledTask scheduled = task;
        task = null;
        targetSelectionPending.set(false);
        if (scheduled != null) {
            scheduled.cancel();
        }
        debug("controller stopped uuid=" + dragon.getUniqueId());
    }

    private void tick(long period) {
        if (!running.get()
                || !dragon.isValid()
                || dragon.isDead()
                || !plugin.enabled("ender-dragon-rework")) {
            stop();
            return;
        }
        hideBossBar();

        EnderDragon.Phase phase = dragon.getPhase();
        if (isForbidden(phase)) {
            dragon.setPhase(EnderDragon.Phase.CIRCLING);
            phase = EnderDragon.Phase.CIRCLING;
        }

        if (charging) {
            chargeTicksRemaining -= period;
            if (chargeTicksRemaining <= 0L || phase != EnderDragon.Phase.CHARGE_PLAYER) {
                finishCharge();
            }
            return;
        }

        cooldownTicks -= period;
        if (cooldownTicks <= 0L && targetSelectionPending.compareAndSet(false, true)) {
            requestTargetSelection();
        }
    }

    private void requestTargetSelection() {
        if (!running.get()) {
            targetSelectionPending.set(false);
            return;
        }

        double range = Math.max(8.0D, plugin.getConfig().getDouble(
                "ender-dragon-rework.dragonling.target-range", 72.0D));
        Location dragonLocation = dragon.getLocation().clone();
        UUID worldId = dragon.getWorld().getUID();
        List<Player> candidates = new ArrayList<>(dragon.getTrackedBy());
        if (candidates.isEmpty()) {
            targetSelectionPending.set(false);
            cooldownTicks = Math.max(20L, randomCooldown() / 2L);
            debug("target scan empty uuid=" + dragon.getUniqueId()
                    + " trackedPlayers=0 nextCooldownTicks=" + cooldownTicks);
            return;
        }

        ConcurrentLinkedQueue<TargetSnapshot> validTargets = new ConcurrentLinkedQueue<>();
        AtomicInteger remaining = new AtomicInteger(candidates.size());
        AtomicBoolean completionScheduled = new AtomicBoolean(false);
        double maximumDistanceSquared = range * range;

        for (Player candidate : candidates) {
            AtomicBoolean completed = new AtomicBoolean(false);
            Runnable finished = () -> {
                if (!completed.compareAndSet(false, true)) {
                    return;
                }
                if (remaining.decrementAndGet() == 0 && completionScheduled.compareAndSet(false, true)) {
                    if (!running.get()) {
                        targetSelectionPending.set(false);
                        return;
                    }
                    ScheduledTask selectionTask = dragon.getScheduler().run(plugin,
                            task -> chooseTarget(validTargets, range, worldId),
                            () -> targetSelectionPending.set(false));
                    if (selectionTask == null) {
                        targetSelectionPending.set(false);
                    }
                }
            };

            ScheduledTask probeTask = candidate.getScheduler().run(plugin, task -> {
                try {
                    if (!running.get()
                            || !candidate.isOnline()
                            || candidate.isDead()
                            || candidate.getHealth() <= 0.0D
                            || candidate.getGameMode() == GameMode.SPECTATOR
                            || candidate.getGameMode() == GameMode.CREATIVE
                            || !candidate.getWorld().getUID().equals(worldId)) {
                        return;
                    }
                    Location playerLocation = candidate.getLocation().clone();
                    if (playerLocation.distanceSquared(dragonLocation) <= maximumDistanceSquared) {
                        validTargets.add(new TargetSnapshot(candidate, playerLocation));
                    }
                } finally {
                    finished.run();
                }
            }, finished);
            if (probeTask == null) {
                finished.run();
            }
        }
    }

    private void chooseTarget(ConcurrentLinkedQueue<TargetSnapshot> targets, double range, UUID worldId) {
        targetSelectionPending.set(false);
        if (!running.get()
                || !dragon.isValid()
                || dragon.isDead()
                || !dragon.getWorld().getUID().equals(worldId)) {
            return;
        }

        Location dragonLocation = dragon.getLocation().clone();
        double maximumDistanceSquared = range * range;
        TargetSnapshot nearest = null;
        double nearestDistanceSquared = maximumDistanceSquared;
        for (TargetSnapshot target : targets) {
            if (target.location().getWorld() == null
                    || !target.location().getWorld().getUID().equals(worldId)) {
                continue;
            }
            double distanceSquared = dragonLocation.distanceSquared(target.location());
            if (distanceSquared < nearestDistanceSquared) {
                nearestDistanceSquared = distanceSquared;
                nearest = target;
            }
        }

        if (nearest == null) {
            cooldownTicks = Math.max(20L, randomCooldown() / 2L);
            debug("target scan no-valid-target uuid=" + dragon.getUniqueId()
                    + " candidates=" + targets.size()
                    + " nextCooldownTicks=" + cooldownTicks);
            return;
        }

        debug("charge start uuid=" + dragon.getUniqueId()
                + " target=" + nearest.player().getName() + "/" + nearest.player().getUniqueId()
                + " distance=" + String.format(java.util.Locale.ROOT, "%.2f", Math.sqrt(nearestDistanceSquared)));
        dragon.setPodium(nearest.location().clone());
        dragon.setTarget(nearest.player());
        dragon.setPhase(EnderDragon.Phase.CHARGE_PLAYER);
        charging = true;
        chargeTicksRemaining = Math.max(10L, plugin.getConfig().getLong(
                "ender-dragon-rework.dragonling.charge-duration-ticks", 50L));
    }

    private void finishCharge() {
        charging = false;
        chargeTicksRemaining = 0L;
        dragon.setTarget(null);
        if (dragon.isValid() && !dragon.isDead()) {
            dragon.setPhase(EnderDragon.Phase.CIRCLING);
        }
        cooldownTicks = randomCooldown();
        debug("charge finish uuid=" + dragon.getUniqueId()
                + " nextCooldownTicks=" + cooldownTicks);
    }

    private long randomCooldown() {
        long minimum = Math.max(1L, plugin.getConfig().getLong(
                "ender-dragon-rework.dragonling.charge-cooldown-min-ticks", 40L));
        long maximum = Math.max(minimum, plugin.getConfig().getLong(
                "ender-dragon-rework.dragonling.charge-cooldown-max-ticks", 80L));
        if (minimum == maximum) {
            return minimum;
        }
        return ThreadLocalRandom.current().nextLong(minimum, maximum + 1L);
    }

    private boolean isForbidden(EnderDragon.Phase phase) {
        return phase == EnderDragon.Phase.STRAFING
                || phase == EnderDragon.Phase.FLY_TO_PORTAL
                || phase == EnderDragon.Phase.LAND_ON_PORTAL
                || phase == EnderDragon.Phase.BREATH_ATTACK
                || phase == EnderDragon.Phase.SEARCH_FOR_BREATH_ATTACK_TARGET
                || phase == EnderDragon.Phase.ROAR_BEFORE_ATTACK;
    }

    private void hideBossBar() {
        if (dragon.getBossBar() != null) {
            dragon.getBossBar().setVisible(false);
        }
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("ender-dragon-rework.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[dragon][dragonling-ai] " + message);
        }
    }

    private record TargetSnapshot(Player player, Location location) {
    }
}
