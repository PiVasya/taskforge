package me.vasya.custommobtweaks.dragon;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.Color;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Particle;
import org.bukkit.World;
import org.bukkit.entity.Phantom;
import org.bukkit.entity.Player;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Lightweight per-phantom controller. Vanilla phantom AI performs the actual
 * circling and swoop; this controller only keeps a valid player target, resets
 * the shared portal anchor and draws the purple dragon aura.
 */
public final class DragonPhantomAttackController {
    private static final Particle.DustTransition PURPLE_AURA = new Particle.DustTransition(
            Color.fromRGB(78, 18, 140),
            Color.fromRGB(196, 74, 255),
            1.35F
    );
    private final CustomMobTweaksPlugin plugin;
    private final Phantom phantom;
    private final Location portalAnchor;
    private final AtomicBoolean running = new AtomicBoolean(false);
    private final AtomicBoolean targetSelectionPending = new AtomicBoolean(false);
    private volatile ScheduledTask task;
    private long targetRefreshTicks;
    private long anchorResetTicks;
    private long auraTicks;
    private UUID lastSelectedTarget;
    private String lastTargetState = "";

    public DragonPhantomAttackController(CustomMobTweaksPlugin plugin, Phantom phantom, Location portalAnchor) {
        this.plugin = plugin;
        this.phantom = phantom;
        this.portalAnchor = portalAnchor.clone();
        this.targetRefreshTicks = randomInitialTargetDelay();
        this.anchorResetTicks = 0L;
        this.auraTicks = 0L;
    }

    public Phantom phantom() {
        return phantom;
    }

    public synchronized void start() {
        if (task != null || !phantom.isValid() || phantom.isDead()) {
            return;
        }
        running.set(true);
        long period = Math.max(2L, configLong("controller-period-ticks", 5L));
        final ScheduledTask[] holder = new ScheduledTask[1];
        ScheduledTask scheduled = phantom.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            holder[0] = scheduledTask;
            tick(period);
        }, () -> {
            running.set(false);
            targetSelectionPending.set(false);
            synchronized (DragonPhantomAttackController.this) {
                if (task == holder[0]) {
                    task = null;
                }
            }
        }, 1L, period);
        task = scheduled;
        if (scheduled == null) {
            running.set(false);
            debug("controller scheduler rejected uuid=" + phantom.getUniqueId());
        } else {
            debug("controller started uuid=" + phantom.getUniqueId()
                    + " periodTicks=" + period
                    + " firstTargetRefreshTicks=" + targetRefreshTicks
                    + " anchor=" + formatLocation(portalAnchor));
        }
    }

    public synchronized void stop() {
        running.set(false);
        targetSelectionPending.set(false);
        ScheduledTask scheduled = task;
        task = null;
        if (scheduled != null) {
            scheduled.cancel();
        }
        debug("controller stopped uuid=" + phantom.getUniqueId());
    }

    private void tick(long period) {
        if (!running.get()
                || !phantom.isValid()
                || phantom.isDead()
                || !plugin.enabled("ender-dragon-rework")) {
            stop();
            return;
        }

        if (phantom.getFireTicks() > 0) {
            phantom.setFireTicks(0);
        }
        if (!phantom.isGlowing()) {
            phantom.setGlowing(true);
        }

        anchorResetTicks -= period;
        if (anchorResetTicks <= 0L) {
            phantom.setAnchorLocation(portalAnchor);
            anchorResetTicks = Math.max(period, configLong("anchor-reset-period-ticks", 40L));
        }

        auraTicks -= period;
        if (auraTicks <= 0L) {
            schedulePurpleAura();
            auraTicks = Math.max(period, configLong("aura.period-ticks", 5L));
        }

        targetRefreshTicks -= period;
        if (targetRefreshTicks <= 0L && targetSelectionPending.compareAndSet(false, true)) {
            requestTargetSelection();
            targetRefreshTicks = Math.max(period, configLong("target-refresh-period-ticks", 20L));
        }
    }

    private void requestTargetSelection() {
        if (!running.get()) {
            targetSelectionPending.set(false);
            return;
        }

        double range = Math.max(8.0D, configDouble("target-range", 72.0D));
        Location phantomLocation = phantom.getLocation().clone();
        UUID worldId = phantom.getWorld().getUID();
        List<Player> candidates = new ArrayList<>(phantom.getTrackedBy());
        if (candidates.isEmpty()) {
            targetSelectionPending.set(false);
            logTargetUnavailable("no-tracked-players", 0);
            phantom.setTarget(null);
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
                    ScheduledTask selectionTask = phantom.getScheduler().run(plugin,
                            task -> chooseTarget(validTargets, worldId),
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
                            || !candidate.getWorld().getUID().equals(worldId)) {
                        return;
                    }
                    Location playerLocation = candidate.getLocation().clone();
                    if (playerLocation.distanceSquared(phantomLocation) <= maximumDistanceSquared) {
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

    private void chooseTarget(ConcurrentLinkedQueue<TargetSnapshot> targets, UUID worldId) {
        targetSelectionPending.set(false);
        if (!running.get()
                || !phantom.isValid()
                || phantom.isDead()
                || !phantom.getWorld().getUID().equals(worldId)) {
            return;
        }

        List<TargetSnapshot> available = new ArrayList<>();
        for (TargetSnapshot target : targets) {
            if (target.location().getWorld() != null
                    && target.location().getWorld().getUID().equals(worldId)) {
                available.add(target);
            }
        }
        if (available.isEmpty()) {
            phantom.setTarget(null);
            logTargetUnavailable("no-valid-target", 0);
            return;
        }

        TargetSnapshot selected = available.get(ThreadLocalRandom.current().nextInt(available.size()));
        UUID selectedId = selected.player().getUniqueId();
        phantom.setAggressive(true);
        phantom.setTarget(selected.player());
        phantom.setAnchorLocation(portalAnchor);
        lastTargetState = "target-available";
        if (!selectedId.equals(lastSelectedTarget)) {
            lastSelectedTarget = selectedId;
            debug("target selected uuid=" + phantom.getUniqueId()
                    + " target=" + selected.player().getName() + "/" + selectedId
                    + " candidates=" + available.size()
                    + " distance=" + String.format(java.util.Locale.ROOT, "%.2f",
                    Math.sqrt(phantom.getLocation().distanceSquared(selected.location()))));
        }
    }

    private void schedulePurpleAura() {
        Location location = phantom.getLocation().clone();
        World world = location.getWorld();
        if (world == null) {
            return;
        }
        int size = Math.max(0, phantom.getSize());
        double spread = 0.55D + size * 0.18D;
        int purpleDust = Math.max(0, configInt("aura.purple-dust-particles", 8));
        int dragonBreath = Math.max(0, configInt("aura.dragon-breath-particles", 6));
        int witch = Math.max(0, configInt("aura.witch-particles", 3));
        int portal = Math.max(0, configInt("aura.portal-particles", 4));
        double verticalSpread = Math.max(0.15D, spread * 0.40D);
        Bukkit.getRegionScheduler().execute(plugin, location, () -> {
            if (purpleDust > 0) {
                world.spawnParticle(Particle.DUST_COLOR_TRANSITION, location,
                        purpleDust, spread, verticalSpread, spread, 0.01D, PURPLE_AURA);
            }
            if (dragonBreath > 0) {
                world.spawnParticle(Particle.DRAGON_BREATH, location,
                        dragonBreath, spread, verticalSpread, spread, 0.015D);
            }
            if (witch > 0) {
                world.spawnParticle(Particle.WITCH, location,
                        witch, spread * 0.8D, verticalSpread, spread * 0.8D, 0.01D);
            }
            if (portal > 0) {
                world.spawnParticle(Particle.PORTAL, location,
                        portal, spread * 0.65D, verticalSpread, spread * 0.65D, 0.04D);
            }
        });
    }

    private void logTargetUnavailable(String reason, int candidates) {
        if (reason.equals(lastTargetState)) {
            return;
        }
        lastTargetState = reason;
        lastSelectedTarget = null;
        debug("target unavailable uuid=" + phantom.getUniqueId()
                + " reason=" + reason
                + " candidates=" + candidates);
    }

    private long randomInitialTargetDelay() {
        long maximum = Math.max(5L, configLong("target-refresh-period-ticks", 20L));
        return ThreadLocalRandom.current().nextLong(1L, maximum + 1L);
    }

    private int configInt(String key, int fallback) {
        return plugin.getConfig().getInt("ender-dragon-rework.phantoms." + key, fallback);
    }

    private long configLong(String key, long fallback) {
        return plugin.getConfig().getLong("ender-dragon-rework.phantoms." + key, fallback);
    }

    private double configDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("ender-dragon-rework.phantoms." + key, fallback);
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("ender-dragon-rework.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[dragon][phantom-ai] " + message);
        }
    }

    private String formatLocation(Location location) {
        return String.format(java.util.Locale.ROOT, "%.2f,%.2f,%.2f",
                location.getX(), location.getY(), location.getZ());
    }

    private record TargetSnapshot(Player player, Location location) {
    }
}
