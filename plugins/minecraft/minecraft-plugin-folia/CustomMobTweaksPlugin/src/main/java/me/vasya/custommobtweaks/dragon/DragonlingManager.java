package me.vasya.custommobtweaks.dragon;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.Chunk;
import org.bukkit.Location;
import org.bukkit.NamespacedKey;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.boss.BossBar;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Entity;
import org.bukkit.event.entity.EnderDragonChangePhaseEvent;
import org.bukkit.persistence.PersistentDataContainer;
import org.bukkit.persistence.PersistentDataType;

import java.util.Collection;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.logging.Level;

/** Creates, marks, monitors, controls and removes true scaled EnderDragon copies. */
public final class DragonlingManager {
    private static final Set<EnderDragon.Phase> FORBIDDEN_DRAGONLING_PHASES = Set.of(
            EnderDragon.Phase.STRAFING,
            EnderDragon.Phase.FLY_TO_PORTAL,
            EnderDragon.Phase.LAND_ON_PORTAL,
            EnderDragon.Phase.BREATH_ATTACK,
            EnderDragon.Phase.SEARCH_FOR_BREATH_ATTACK_TARGET,
            EnderDragon.Phase.ROAR_BEFORE_ATTACK
    );

    private static final Set<EnderDragon.Phase> LANDING_FAMILY = Set.of(
            EnderDragon.Phase.FLY_TO_PORTAL,
            EnderDragon.Phase.LAND_ON_PORTAL,
            EnderDragon.Phase.BREATH_ATTACK,
            EnderDragon.Phase.SEARCH_FOR_BREATH_ATTACK_TARGET,
            EnderDragon.Phase.ROAR_BEFORE_ATTACK
    );

    private static final Set<EnderDragon.Phase> LANDED_PHASES = Set.of(
            EnderDragon.Phase.LAND_ON_PORTAL,
            EnderDragon.Phase.BREATH_ATTACK,
            EnderDragon.Phase.SEARCH_FOR_BREATH_ATTACK_TARGET,
            EnderDragon.Phase.ROAR_BEFORE_ATTACK
    );

    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey dragonlingKey = new NamespacedKey("taskforge", "dragonling");
    private final NamespacedKey ownerKey = new NamespacedKey("taskforge", "dragonling_owner");
    private final Map<UUID, DragonlingAttackController> controllers = new ConcurrentHashMap<>();
    private final Map<UUID, Set<UUID>> dragonlingsByOwner = new ConcurrentHashMap<>();
    private final Map<UUID, Object> ownerLocks = new ConcurrentHashMap<>();
    private final Map<UUID, PrimaryMonitor> primaryMonitors = new ConcurrentHashMap<>();
    private final Set<UUID> retiredOwners = ConcurrentHashMap.newKeySet();
    private final AtomicInteger startupScanTasks = new AtomicInteger();
    private final AtomicInteger startupPrimaryDragons = new AtomicInteger();
    private final AtomicInteger startupOrphanDragonlings = new AtomicInteger();
    private volatile boolean running;

    public DragonlingManager(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    public void start() {
        running = true;
        debug("manager start scale=" + configDouble("scale", 0.25D)
                + " health=" + configDouble("health", 40.0D)
                + " maximumActive=" + configInt("maximum-active", 2)
                + " landingMonitorPeriodTicks=" + configLong("landing-monitor-period-ticks", 5L));
        inspectLoadedDragons();
    }

    public void shutdown() {
        running = false;

        for (PrimaryMonitor monitor : Set.copyOf(primaryMonitors.values())) {
            monitor.stop("shutdown");
        }
        primaryMonitors.clear();

        for (DragonlingAttackController controller : Set.copyOf(controllers.values())) {
            controller.stop();
            removeOnEntityThread(controller.dragon(), "shutdown");
        }
        controllers.clear();
        dragonlingsByOwner.clear();
        retiredOwners.clear();
        ownerLocks.clear();
        debug("manager stopped");
    }

    public boolean isDragonling(EnderDragon dragon) {
        return dragon.getPersistentDataContainer().has(dragonlingKey, PersistentDataType.BYTE);
    }

    /** Every unmarked EnderDragon is treated as a primary dragon; DragonBattle may legally be null. */
    public boolean isPrimaryDragon(EnderDragon dragon) {
        return !isDragonling(dragon);
    }

    public void handleDragonAdded(EnderDragon dragon, String source) {
        if (!running) {
            return;
        }
        dragon.getScheduler().runDelayed(plugin, task -> {
            if (!running || !dragon.isValid() || dragon.isDead()) {
                return;
            }
            if (isDragonling(dragon)) {
                DragonlingAttackController controller = controllers.get(dragon.getUniqueId());
                if (controller == null) {
                    debug("orphan dragonling detected source=" + source
                            + " uuid=" + dragon.getUniqueId() + " action=remove");
                    dragon.remove();
                } else {
                    controller.start();
                }
                return;
            }
            observePrimary(dragon, source);
        }, () -> debug("dragon retired before add inspection source=" + source
                + " uuid=" + dragon.getUniqueId()), 1L);
    }

    public void handleEntitiesLoad(Collection<Entity> entities) {
        if (!running) {
            return;
        }
        for (Entity entity : entities) {
            if (entity instanceof EnderDragon dragon) {
                handleDragonAdded(dragon, "entities-load");
            }
        }
    }

    public void handlePhaseChange(EnderDragonChangePhaseEvent event) {
        EnderDragon dragon = event.getEntity();
        EnderDragon.Phase oldPhase = event.getCurrentPhase();
        EnderDragon.Phase requestedPhase = event.getNewPhase();

        if (isDragonling(dragon)) {
            if (FORBIDDEN_DRAGONLING_PHASES.contains(requestedPhase)) {
                debug("dragonling forbidden phase uuid=" + dragon.getUniqueId()
                        + " old=" + oldPhase + " requested=" + requestedPhase + " forced=CIRCLING");
                event.setNewPhase(EnderDragon.Phase.CIRCLING);
            }
            return;
        }

        observePrimary(dragon, "phase-event");
        debug("primary phase event uuid=" + dragon.getUniqueId()
                + " old=" + oldPhase
                + " new=" + requestedPhase
                + " cancelled=" + event.isCancelled()
                + " battlePresent=" + (dragon.getDragonBattle() != null)
                + " location=" + formatLocation(dragon.getLocation()));

        PrimaryMonitor monitor = primaryMonitors.get(dragon.getUniqueId());
        if (monitor != null) {
            monitor.processPhase(requestedPhase, "phase-event");
        }
    }

    public void handlePrimaryDragonDeath(EnderDragon owner) {
        UUID ownerId = owner.getUniqueId();
        retiredOwners.add(ownerId);
        PrimaryMonitor monitor = primaryMonitors.remove(ownerId);
        if (monitor != null) {
            monitor.stop("primary-death");
        }
        int count = activeCount(ownerId);
        debug("primary death uuid=" + ownerId + " ownedDragonlings=" + count + " action=remove-owned");
        removeOwnedDragonlings(ownerId);
    }

    public void handleDragonlingDeath(EnderDragon dragonling) {
        UUID ownerId = ownerOf(dragonling);
        debug("dragonling death uuid=" + dragonling.getUniqueId() + " owner=" + ownerId);
        unregister(dragonling.getUniqueId(), ownerId);
    }

    private void observePrimary(EnderDragon dragon, String source) {
        if (!running || !dragon.isValid() || dragon.isDead() || isDragonling(dragon)) {
            return;
        }
        PrimaryMonitor existing = primaryMonitors.get(dragon.getUniqueId());
        if (existing != null) {
            return;
        }

        PrimaryMonitor created = new PrimaryMonitor(dragon);
        PrimaryMonitor raced = primaryMonitors.putIfAbsent(dragon.getUniqueId(), created);
        if (raced != null) {
            return;
        }
        if ("startup-loaded-chunk".equals(source)) {
            startupPrimaryDragons.incrementAndGet();
        }
        debug("primary observed source=" + source
                + " uuid=" + dragon.getUniqueId()
                + " world=" + dragon.getWorld().getKey()
                + " phase=" + dragon.getPhase()
                + " battlePresent=" + (dragon.getDragonBattle() != null)
                + " podium=" + formatLocation(dragon.getPodium())
                + " location=" + formatLocation(dragon.getLocation()));
        created.start();
    }

    private void trySpawnDragonling(EnderDragon owner, int landingCycle, String source) {
        UUID ownerId = owner.getUniqueId();
        int maximumActive = Math.max(0, configInt("maximum-active", 2));
        int current = activeCount(ownerId);
        if (!running) {
            debug("dragonling spawn skipped owner=" + ownerId + " cycle=" + landingCycle + " reason=manager-stopped");
            return;
        }
        if (retiredOwners.contains(ownerId)) {
            debug("dragonling spawn skipped owner=" + ownerId + " cycle=" + landingCycle + " reason=owner-retired");
            return;
        }
        if (maximumActive == 0) {
            debug("dragonling spawn skipped owner=" + ownerId + " cycle=" + landingCycle + " reason=maximum-active-zero");
            return;
        }
        if (current >= maximumActive) {
            debug("dragonling spawn skipped owner=" + ownerId + " cycle=" + landingCycle
                    + " reason=limit current=" + current + " maximum=" + maximumActive);
            return;
        }

        Location ownerLocation = owner.getLocation().clone();
        Location podium = owner.getPodium().clone();
        Location spawnAnchor = podium.getWorld() == ownerLocation.getWorld() ? podium : ownerLocation;
        double angle = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
        double spawnRadius = Math.max(3.0D, configDouble("spawn-radius", 7.0D));
        double spawnHeight = Math.max(2.0D, configDouble("spawn-height", 5.0D));
        Location spawnLocation = spawnAnchor.clone().add(
                Math.cos(angle) * spawnRadius,
                spawnHeight,
                Math.sin(angle) * spawnRadius
        );
        debug("dragonling spawn dispatch owner=" + ownerId
                + " cycle=" + landingCycle
                + " source=" + source
                + " phase=" + owner.getPhase()
                + " ownerLocation=" + formatLocation(ownerLocation)
                + " podium=" + formatLocation(podium)
                + " spawnLocation=" + formatLocation(spawnLocation)
                + " active=" + current + "/" + maximumActive);

        Bukkit.getRegionScheduler().execute(plugin, spawnLocation,
                () -> spawnDragonling(spawnLocation, ownerId, landingCycle));
    }

    private void spawnDragonling(Location spawnLocation, UUID ownerId, int landingCycle) {
        if (!running || spawnLocation.getWorld() == null) {
            debug("dragonling spawn aborted owner=" + ownerId + " cycle=" + landingCycle
                    + " reason=runtime-or-world");
            return;
        }
        int maximumActive = Math.max(0, configInt("maximum-active", 2));
        int before = activeCount(ownerId);
        if (retiredOwners.contains(ownerId) || maximumActive == 0 || before >= maximumActive) {
            debug("dragonling spawn aborted owner=" + ownerId + " cycle=" + landingCycle
                    + " reason=second-limit-check active=" + before + "/" + maximumActive);
            return;
        }

        EnderDragon dragonling = null;
        try {
            dragonling = spawnLocation.getWorld().spawn(spawnLocation, EnderDragon.class, created -> {
                PersistentDataContainer pdc = created.getPersistentDataContainer();
                pdc.set(dragonlingKey, PersistentDataType.BYTE, (byte) 1);
                pdc.set(ownerKey, PersistentDataType.STRING, ownerId.toString());
            });

            double scale = clamp(configDouble("scale", 0.25D), 0.0625D, 4.0D);
            double health = Math.max(1.0D, configDouble("health", 40.0D));
            setBaseAttribute(dragonling, Attribute.SCALE, scale);
            setBaseAttribute(dragonling, Attribute.MAX_HEALTH, health);
            dragonling.setHealth(health);
            dragonling.setPersistent(true);
            dragonling.clearLootTable();
            dragonling.setAware(true);
            dragonling.setAggressive(true);
            dragonling.setRemoveWhenFarAway(false);
            dragonling.setCustomNameVisible(false);
            dragonling.setTarget(null);
            dragonling.setPodium(spawnLocation.clone());
            dragonling.setPhase(EnderDragon.Phase.CIRCLING);
            hideBossBar(dragonling);

            DragonlingAttackController controller = new DragonlingAttackController(plugin, dragonling);
            Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
            boolean registered;
            synchronized (ownerLock) {
                int currentCount = activeCount(ownerId);
                registered = running
                        && !retiredOwners.contains(ownerId)
                        && currentCount < maximumActive;
                if (registered) {
                    controllers.put(dragonling.getUniqueId(), controller);
                    dragonlingsByOwner.computeIfAbsent(ownerId, ignored -> ConcurrentHashMap.newKeySet())
                            .add(dragonling.getUniqueId());
                }
            }
            if (!registered) {
                debug("dragonling spawned but registration lost race uuid=" + dragonling.getUniqueId()
                        + " owner=" + ownerId + " action=remove");
                dragonling.remove();
                return;
            }

            debug("dragonling spawned uuid=" + dragonling.getUniqueId()
                    + " owner=" + ownerId
                    + " cycle=" + landingCycle
                    + " scale=" + scale
                    + " health=" + health
                    + " battlePresent=" + (dragonling.getDragonBattle() != null)
                    + " location=" + formatLocation(dragonling.getLocation())
                    + " activeNow=" + activeCount(ownerId) + "/" + maximumActive);
            controller.start();
        } catch (Throwable error) {
            if (dragonling != null && dragonling.isValid()) {
                dragonling.remove();
            }
            plugin.getLogger().log(Level.SEVERE,
                    "[dragon][dragonling] spawn failed owner=" + ownerId
                            + " cycle=" + landingCycle
                            + " location=" + formatLocation(spawnLocation), error);
        }
    }

    private void hideBossBar(EnderDragon dragonling) {
        BossBar bossBar = dragonling.getBossBar();
        if (bossBar != null) {
            bossBar.setVisible(false);
        }
    }

    private void removeOwnedDragonlings(UUID ownerId) {
        Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
        Set<DragonlingAttackController> removedControllers = ConcurrentHashMap.newKeySet();
        synchronized (ownerLock) {
            Set<UUID> ids = dragonlingsByOwner.remove(ownerId);
            if (ids != null) {
                for (UUID dragonlingId : Set.copyOf(ids)) {
                    DragonlingAttackController controller = controllers.remove(dragonlingId);
                    if (controller != null) {
                        removedControllers.add(controller);
                    }
                }
            }
        }
        for (DragonlingAttackController controller : removedControllers) {
            controller.stop();
            removeOnEntityThread(controller.dragon(), "primary-death");
        }
        ownerLocks.remove(ownerId, ownerLock);
    }

    private void unregister(UUID dragonlingId, UUID ownerId) {
        DragonlingAttackController controller;
        if (ownerId == null) {
            controller = controllers.remove(dragonlingId);
        } else {
            Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
            synchronized (ownerLock) {
                controller = controllers.remove(dragonlingId);
                Set<UUID> ids = dragonlingsByOwner.get(ownerId);
                if (ids != null) {
                    ids.remove(dragonlingId);
                    if (ids.isEmpty()) {
                        dragonlingsByOwner.remove(ownerId, ids);
                    }
                }
            }
            if (!dragonlingsByOwner.containsKey(ownerId)) {
                ownerLocks.remove(ownerId, ownerLock);
            }
        }
        if (controller != null) {
            controller.stop();
        }
    }

    private UUID ownerOf(EnderDragon dragonling) {
        String raw = dragonling.getPersistentDataContainer().get(ownerKey, PersistentDataType.STRING);
        if (raw == null) {
            return null;
        }
        try {
            return UUID.fromString(raw);
        } catch (IllegalArgumentException ignored) {
            return null;
        }
    }

    private int activeCount(UUID ownerId) {
        Set<UUID> ids = dragonlingsByOwner.get(ownerId);
        return ids == null ? 0 : ids.size();
    }

    private void removeOnEntityThread(EnderDragon dragonling, String reason) {
        dragonling.getScheduler().run(plugin, task -> {
            if (isDragonling(dragonling) && dragonling.isValid()) {
                debug("dragonling remove uuid=" + dragonling.getUniqueId() + " reason=" + reason);
                dragonling.remove();
            }
        }, () -> { });
    }

    private void inspectLoadedDragons() {
        int chunks = 0;
        for (World world : Bukkit.getWorlds()) {
            for (Chunk chunk : world.getLoadedChunks()) {
                chunks++;
                startupScanTasks.incrementAndGet();
                Location anchor = new Location(
                        world,
                        (chunk.getX() << 4) + 8.0D,
                        world.getMinHeight() + 1.0D,
                        (chunk.getZ() << 4) + 8.0D
                );
                Bukkit.getRegionScheduler().execute(plugin, anchor, () -> {
                    try {
                        if (!running) {
                            return;
                        }
                        for (Entity entity : chunk.getEntities()) {
                            if (!(entity instanceof EnderDragon dragon)) {
                                continue;
                            }
                            if (isDragonling(dragon)) {
                                if (!controllers.containsKey(dragon.getUniqueId())) {
                                    startupOrphanDragonlings.incrementAndGet();
                                    debug("startup orphan dragonling uuid=" + dragon.getUniqueId() + " action=remove");
                                    dragon.remove();
                                }
                            } else {
                                observePrimary(dragon, "startup-loaded-chunk");
                            }
                        }
                    } finally {
                        if (startupScanTasks.decrementAndGet() == 0) {
                            debug("startup loaded-chunk inspection complete primaryDragons="
                                    + startupPrimaryDragons.get()
                                    + " orphanDragonlingsRemoved=" + startupOrphanDragonlings.get()
                                    + " hint=" + (startupPrimaryDragons.get() == 0
                                    ? "no-primary-currently-loaded; entity-add/load events remain armed"
                                    : "primary-monitors-active"));
                        }
                    }
                });
            }
        }
        debug("startup loaded-chunk inspection scheduled chunks=" + chunks);
        if (chunks == 0) {
            debug("startup loaded-chunk inspection complete primaryDragons=0 orphanDragonlingsRemoved=0"
                    + " hint=no-loaded-chunks; entity-add/load events remain armed");
        }
    }

    private void setBaseAttribute(EnderDragon dragon, Attribute attribute, double value) {
        AttributeInstance instance = dragon.getAttribute(attribute);
        if (instance == null) {
            dragon.registerAttribute(attribute);
            instance = dragon.getAttribute(attribute);
        }
        if (instance == null) {
            throw new IllegalStateException("EnderDragon is missing required attribute " + attribute.getKey());
        }
        instance.setBaseValue(value);
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("ender-dragon-rework.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[dragon][dragonling] " + message);
        }
    }

    private int configInt(String key, int fallback) {
        return plugin.getConfig().getInt("ender-dragon-rework.dragonling." + key, fallback);
    }

    private long configLong(String key, long fallback) {
        return plugin.getConfig().getLong("ender-dragon-rework.dragonling." + key, fallback);
    }

    private double configDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("ender-dragon-rework.dragonling." + key, fallback);
    }

    private double clamp(double value, double minimum, double maximum) {
        return Math.max(minimum, Math.min(maximum, value));
    }

    private String formatLocation(Location location) {
        return String.format(java.util.Locale.ROOT, "%.2f,%.2f,%.2f",
                location.getX(), location.getY(), location.getZ());
    }

    private final class PrimaryMonitor {
        private final EnderDragon dragon;
        private volatile ScheduledTask task;
        private EnderDragon.Phase lastPhase;
        private boolean landingCycleActive;
        private boolean spawnAttempted;
        private int landingCycle;

        private PrimaryMonitor(EnderDragon dragon) {
            this.dragon = dragon;
            this.lastPhase = dragon.getPhase();
        }

        private synchronized void start() {
            if (task != null || !running || !dragon.isValid() || dragon.isDead()) {
                return;
            }
            long period = Math.max(2L, configLong("landing-monitor-period-ticks", 5L));
            final ScheduledTask[] holder = new ScheduledTask[1];
            ScheduledTask scheduled = dragon.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
                holder[0] = scheduledTask;
                if (!running || !dragon.isValid() || dragon.isDead() || isDragonling(dragon)) {
                    stop("retired-or-reclassified");
                    return;
                }
                EnderDragon.Phase phase = dragon.getPhase();
                if (phase != lastPhase) {
                    debug("primary monitor phase uuid=" + dragon.getUniqueId()
                            + " old=" + lastPhase + " new=" + phase
                            + " location=" + formatLocation(dragon.getLocation()));
                    lastPhase = phase;
                }
                processPhase(phase, "entity-monitor");
            }, () -> {
                primaryMonitors.remove(dragon.getUniqueId(), this);
                synchronized (PrimaryMonitor.this) {
                    if (task == holder[0]) {
                        task = null;
                    }
                }
                debug("primary monitor retired uuid=" + dragon.getUniqueId());
            }, 1L, period);
            holder[0] = scheduled;
            task = scheduled;
            if (scheduled == null) {
                primaryMonitors.remove(dragon.getUniqueId(), this);
                debug("primary monitor scheduler rejected uuid=" + dragon.getUniqueId());
            } else {
                processPhase(dragon.getPhase(), "monitor-start");
            }
        }

        private synchronized void processPhase(EnderDragon.Phase phase, String source) {
            if (!running || !dragon.isValid() || dragon.isDead() || isDragonling(dragon)) {
                return;
            }
            if (LANDING_FAMILY.contains(phase)) {
                if (!landingCycleActive) {
                    landingCycleActive = true;
                    spawnAttempted = false;
                    landingCycle++;
                    debug("landing cycle start owner=" + dragon.getUniqueId()
                            + " cycle=" + landingCycle
                            + " source=" + source
                            + " phase=" + phase
                            + " podium=" + formatLocation(dragon.getPodium())
                            + " location=" + formatLocation(dragon.getLocation()));
                }
                if (LANDED_PHASES.contains(phase) && !spawnAttempted) {
                    spawnAttempted = true;
                    debug("landing confirmed owner=" + dragon.getUniqueId()
                            + " cycle=" + landingCycle
                            + " source=" + source
                            + " phase=" + phase
                            + " action=spawn-one");
                    trySpawnDragonling(dragon, landingCycle, source);
                }
                return;
            }

            if (landingCycleActive) {
                debug("landing cycle end owner=" + dragon.getUniqueId()
                        + " cycle=" + landingCycle
                        + " source=" + source
                        + " phase=" + phase
                        + " spawnAttempted=" + spawnAttempted);
            }
            landingCycleActive = false;
            spawnAttempted = false;
        }

        private synchronized void stop(String reason) {
            ScheduledTask scheduled = task;
            task = null;
            if (scheduled != null) {
                scheduled.cancel();
            }
            primaryMonitors.remove(dragon.getUniqueId(), this);
            debug("primary monitor stop uuid=" + dragon.getUniqueId() + " reason=" + reason);
        }
    }
}
