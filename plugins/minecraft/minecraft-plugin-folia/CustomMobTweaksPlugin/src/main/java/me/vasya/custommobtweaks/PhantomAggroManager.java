package me.vasya.custommobtweaks;

import com.destroystokyo.paper.event.entity.EntityAddToWorldEvent;
import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.NamespacedKey;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Phantom;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.CreatureSpawnEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.EntityRemoveEvent;
import org.bukkit.event.world.EntitiesLoadEvent;
import org.bukkit.persistence.PersistentDataType;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Makes manually spawned phantoms behave like real hostile phantoms.
 *
 * Vanilla phantoms created from a spawn egg can remain without a target for a
 * long time. This controller only manages explicitly marked manual phantoms;
 * natural phantoms, dragon phantoms and short-lived dive clones keep their own
 * behaviour.
 */
public final class PhantomAggroManager implements PluginComponent, Listener {
    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey managedKey;
    private final NamespacedKey diveCloneKey;
    private final NamespacedKey dragonPhantomKey = new NamespacedKey("taskforge", "dragon_phantom");
    private final Map<UUID, AggroController> controllers = new ConcurrentHashMap<>();
    private volatile boolean running;

    public PhantomAggroManager(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.managedKey = new NamespacedKey(plugin, "manual_phantom_aggro");
        this.diveCloneKey = new NamespacedKey(plugin, "phantom_dive_clone");
    }

    @Override
    public void start() {
        running = true;
        Bukkit.getPluginManager().registerEvents(this, plugin);
        inspectLoadedPhantoms();
        debug("manager started targetRange=" + configDouble("target-range", 96.0D)
                + " controllerPeriodTicks=" + configLong("controller-period-ticks", 10L)
                + " targetRefreshPeriodTicks=" + configLong("target-refresh-period-ticks", 20L)
                + " spawnEgg=" + configBoolean("manage-spawner-egg", true)
                + " command=" + configBoolean("manage-command", true)
                + " custom=" + configBoolean("manage-custom", false));
    }

    @Override
    public void shutdown() {
        running = false;
        HandlerList.unregisterAll(this);
        for (AggroController controller : Set.copyOf(controllers.values())) {
            controller.stop("shutdown");
        }
        controllers.clear();
        debug("manager stopped");
    }

    public String diagnosticsSummary() {
        return "phantom-aggro=" + (plugin.enabled("phantom-aggro") ? "enabled" : "disabled")
                + ",managed=" + controllers.size()
                + ",targetRange=" + configDouble("target-range", 96.0D);
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onCreatureSpawn(CreatureSpawnEvent event) {
        if (!(event.getEntity() instanceof Phantom phantom) || !shouldManage(event.getSpawnReason())) {
            return;
        }
        phantom.getPersistentDataContainer().set(managedKey, PersistentDataType.BYTE, (byte) 1);
        scheduleControllerStart(phantom, "spawn:" + event.getSpawnReason().name().toLowerCase());
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onEntityAdded(EntityAddToWorldEvent event) {
        if (event.getEntity() instanceof Phantom phantom && isManaged(phantom)) {
            scheduleControllerStart(phantom, "entity-add");
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onEntitiesLoad(EntitiesLoadEvent event) {
        for (Entity entity : event.getEntities()) {
            if (entity instanceof Phantom phantom && isManaged(phantom)) {
                scheduleControllerStart(phantom, "entities-load");
            }
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onPhantomDeath(EntityDeathEvent event) {
        if (event.getEntity() instanceof Phantom phantom) {
            stopController(phantom.getUniqueId(), "death");
        }
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onPhantomRemove(EntityRemoveEvent event) {
        if (event.getEntity() instanceof Phantom phantom) {
            stopController(phantom.getUniqueId(), "remove");
        }
    }

    private boolean shouldManage(CreatureSpawnEvent.SpawnReason reason) {
        if (!running || !plugin.enabled("phantom-aggro")) {
            return false;
        }
        return switch (reason) {
            case SPAWNER_EGG -> configBoolean("manage-spawner-egg", true);
            case COMMAND -> configBoolean("manage-command", true);
            case CUSTOM -> configBoolean("manage-custom", false);
            default -> false;
        };
    }

    private void scheduleControllerStart(Phantom phantom, String source) {
        if (!running) {
            return;
        }
        phantom.getScheduler().runDelayed(plugin, task -> {
            if (!running || !plugin.enabled("phantom-aggro") || !phantom.isValid() || phantom.isDead()) {
                return;
            }
            if (!isManaged(phantom) || isDiveClone(phantom) || isDragonPhantom(phantom)) {
                stopController(phantom.getUniqueId(), "excluded:" + source);
                return;
            }
            AggroController created = new AggroController(phantom);
            AggroController existing = controllers.putIfAbsent(phantom.getUniqueId(), created);
            if (existing == null) {
                created.start();
                debug("phantom managed source=" + source + " uuid=" + phantom.getUniqueId()
                        + " size=" + phantom.getSize());
            }
        }, () -> stopController(phantom.getUniqueId(), "retired-before-start:" + source), 2L);
    }

    private void inspectLoadedPhantoms() {
        for (var world : Bukkit.getWorlds()) {
            for (var chunk : world.getLoadedChunks()) {
                Location regionPoint = new Location(world,
                        (chunk.getX() << 4) + 8.0D,
                        world.getMinHeight() + 1.0D,
                        (chunk.getZ() << 4) + 8.0D);
                Bukkit.getRegionScheduler().execute(plugin, regionPoint, () -> {
                    if (!running) {
                        return;
                    }
                    for (Entity entity : chunk.getEntities()) {
                        if (entity instanceof Phantom phantom && isManaged(phantom)) {
                            scheduleControllerStart(phantom, "startup-loaded-chunk");
                        }
                    }
                });
            }
        }
    }

    private void stopController(UUID phantomId, String reason) {
        AggroController controller = controllers.remove(phantomId);
        if (controller != null) {
            controller.stop(reason);
        }
    }

    private boolean isManaged(Phantom phantom) {
        return phantom.getPersistentDataContainer().has(managedKey, PersistentDataType.BYTE);
    }

    private boolean isDiveClone(Phantom phantom) {
        return phantom.getPersistentDataContainer().has(diveCloneKey, PersistentDataType.BYTE);
    }

    private boolean isDragonPhantom(Phantom phantom) {
        return phantom.getPersistentDataContainer().has(dragonPhantomKey, PersistentDataType.BYTE);
    }

    private boolean eligible(Player player, UUID worldId) {
        GameMode gameMode = player.getGameMode();
        return player.isOnline()
                && !player.isDead()
                && player.getHealth() > 0.0D
                && (gameMode == GameMode.SURVIVAL || gameMode == GameMode.ADVENTURE)
                && player.getWorld().getUID().equals(worldId);
    }

    private long configLong(String key, long fallback) {
        return plugin.getConfig().getLong("phantom-aggro." + key, fallback);
    }

    private double configDouble(String key, double fallback) {
        return plugin.getConfig().getDouble("phantom-aggro." + key, fallback);
    }

    private boolean configBoolean(String key, boolean fallback) {
        return plugin.getConfig().getBoolean("phantom-aggro." + key, fallback);
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("phantom-aggro.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[phantom-aggro] " + message);
        }
    }

    private final class AggroController {
        private final Phantom phantom;
        private final AtomicBoolean runningController = new AtomicBoolean(false);
        private final AtomicBoolean targetSelectionPending = new AtomicBoolean(false);
        private volatile ScheduledTask task;
        private long targetRefreshTicks;
        private UUID lastTargetId;

        private AggroController(Phantom phantom) {
            this.phantom = phantom;
        }

        private synchronized void start() {
            if (task != null || !phantom.isValid() || phantom.isDead()) {
                return;
            }
            runningController.set(true);
            targetRefreshTicks = 0L;
            long period = Math.max(2L, configLong("controller-period-ticks", 10L));
            final ScheduledTask[] holder = new ScheduledTask[1];
            ScheduledTask scheduled = phantom.getScheduler().runAtFixedRate(plugin, current -> {
                holder[0] = current;
                tick(period);
            }, () -> {
                runningController.set(false);
                targetSelectionPending.set(false);
                synchronized (AggroController.this) {
                    if (task == holder[0]) {
                        task = null;
                    }
                }
                controllers.remove(phantom.getUniqueId(), AggroController.this);
            }, 1L, period);
            task = scheduled;
            if (scheduled == null) {
                runningController.set(false);
                controllers.remove(phantom.getUniqueId(), this);
                debug("controller scheduler rejected uuid=" + phantom.getUniqueId());
            }
        }

        private void tick(long period) {
            if (!runningController.get()
                    || !running
                    || !plugin.enabled("phantom-aggro")
                    || !phantom.isValid()
                    || phantom.isDead()
                    || !isManaged(phantom)
                    || isDiveClone(phantom)
                    || isDragonPhantom(phantom)) {
                stop("invalid-or-disabled");
                return;
            }

            phantom.setAware(true);
            phantom.setAggressive(true);

            boolean targetMissing = phantom.getTarget() == null;

            targetRefreshTicks -= period;
            if ((targetMissing || targetRefreshTicks <= 0L)
                    && targetSelectionPending.compareAndSet(false, true)) {
                requestTargetSelection();
                targetRefreshTicks = Math.max(period, configLong("target-refresh-period-ticks", 20L));
            }
        }

        private void requestTargetSelection() {
            if (!runningController.get()) {
                targetSelectionPending.set(false);
                return;
            }

            double range = Math.max(8.0D, configDouble("target-range", 96.0D));
            Location phantomLocation = phantom.getLocation().clone();
            UUID worldId = phantom.getWorld().getUID();
            List<Player> candidates = new ArrayList<>(phantom.getTrackedBy());
            if (candidates.isEmpty()) {
                targetSelectionPending.set(false);
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
                        if (!runningController.get()) {
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
                        if (!runningController.get() || !eligible(candidate, worldId)) {
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
            if (!runningController.get()
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
                return;
            }

            TargetSnapshot selected = available.stream()
                    .min(Comparator.comparingDouble(target -> phantom.getLocation().distanceSquared(target.location())))
                    .orElse(available.getFirst());
            phantom.setAware(true);
            phantom.setAggressive(true);
            phantom.setAnchorLocation(selected.location().clone());
            phantom.setTarget(selected.player());

            UUID selectedId = selected.player().getUniqueId();
            if (!selectedId.equals(lastTargetId)) {
                lastTargetId = selectedId;
                debug("target selected phantom=" + phantom.getUniqueId()
                        + " target=" + selected.player().getName() + "/" + selectedId
                        + " candidates=" + available.size()
                        + " distance=" + String.format(java.util.Locale.ROOT, "%.2f",
                        Math.sqrt(phantom.getLocation().distanceSquared(selected.location()))));
            }
        }

        private synchronized void stop(String reason) {
            runningController.set(false);
            targetSelectionPending.set(false);
            ScheduledTask scheduled = task;
            task = null;
            if (scheduled != null) {
                scheduled.cancel();
            }
            controllers.remove(phantom.getUniqueId(), this);
            debug("controller stopped uuid=" + phantom.getUniqueId() + " reason=" + reason);
        }
    }

    private record TargetSnapshot(Player player, Location location) {
    }
}
