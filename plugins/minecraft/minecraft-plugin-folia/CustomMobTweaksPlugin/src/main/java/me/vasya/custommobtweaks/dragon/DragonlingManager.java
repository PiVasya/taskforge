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

/** Creates, marks, controls and removes true scaled EnderDragon copies. */
public final class DragonlingManager {
    private static final Set<EnderDragon.Phase> FORBIDDEN_PHASES = Set.of(
            EnderDragon.Phase.STRAFING,
            EnderDragon.Phase.FLY_TO_PORTAL,
            EnderDragon.Phase.LAND_ON_PORTAL,
            EnderDragon.Phase.BREATH_ATTACK,
            EnderDragon.Phase.SEARCH_FOR_BREATH_ATTACK_TARGET,
            EnderDragon.Phase.ROAR_BEFORE_ATTACK
    );

    private static final Set<EnderDragon.Phase> LANDING_ACTIVE_PHASES = Set.of(
            EnderDragon.Phase.FLY_TO_PORTAL,
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
    private final Set<UUID> retiredOwners = ConcurrentHashMap.newKeySet();
    private final Set<UUID> activeLandings = ConcurrentHashMap.newKeySet();
    private final Set<UUID> landingSpawnAttempted = ConcurrentHashMap.newKeySet();
    private final Map<UUID, ScheduledTask> landingTasks = new ConcurrentHashMap<>();
    private volatile boolean running;

    public DragonlingManager(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    public void start() {
        running = true;
        removeMarkedDragonlingsFromLoadedChunks();
    }

    public void shutdown() {
        running = false;
        activeLandings.clear();
        landingSpawnAttempted.clear();
        for (ScheduledTask task : Set.copyOf(landingTasks.values())) {
            task.cancel();
        }
        landingTasks.clear();

        for (DragonlingAttackController controller : Set.copyOf(controllers.values())) {
            controller.stop();
            removeOnEntityThread(controller.dragon());
        }
        controllers.clear();
        dragonlingsByOwner.clear();
        retiredOwners.clear();
        ownerLocks.clear();
    }

    public boolean isDragonling(EnderDragon dragon) {
        return dragon.getPersistentDataContainer().has(dragonlingKey, PersistentDataType.BYTE);
    }

    public boolean isPrimaryDragon(EnderDragon dragon) {
        return !isDragonling(dragon) && dragon.getDragonBattle() != null;
    }

    public void handlePhaseChange(EnderDragonChangePhaseEvent event) {
        EnderDragon dragon = event.getEntity();
        if (isDragonling(dragon)) {
            if (FORBIDDEN_PHASES.contains(event.getNewPhase())) {
                event.setNewPhase(EnderDragon.Phase.CIRCLING);
            }
            return;
        }
        if (!isPrimaryDragon(dragon)) {
            return;
        }

        UUID ownerId = dragon.getUniqueId();
        EnderDragon.Phase next = event.getNewPhase();
        if (next == EnderDragon.Phase.LAND_ON_PORTAL && activeLandings.add(ownerId)) {
            landingSpawnAttempted.remove(ownerId);
            startLandingWatcher(dragon);
        } else if (!LANDING_ACTIVE_PHASES.contains(next)) {
            activeLandings.remove(ownerId);
            landingSpawnAttempted.remove(ownerId);
            cancelLandingWatcher(ownerId);
        }
    }

    public void handlePrimaryDragonDeath(EnderDragon owner) {
        UUID ownerId = owner.getUniqueId();
        retiredOwners.add(ownerId);
        activeLandings.remove(ownerId);
        landingSpawnAttempted.remove(ownerId);
        cancelLandingWatcher(ownerId);
        removeOwnedDragonlings(ownerId);
    }

    public void handleDragonlingDeath(EnderDragon dragonling) {
        unregister(dragonling.getUniqueId(), ownerOf(dragonling));
    }

    public void handleEntitiesLoad(Collection<Entity> entities) {
        if (!running) {
            return;
        }
        for (Entity entity : entities) {
            if (!(entity instanceof EnderDragon dragon) || !isDragonling(dragon)) {
                continue;
            }
            DragonlingAttackController controller = controllers.get(dragon.getUniqueId());
            if (controller == null) {
                dragon.remove();
            } else {
                controller.start();
            }
        }
    }

    private void startLandingWatcher(EnderDragon owner) {
        UUID ownerId = owner.getUniqueId();
        cancelLandingWatcher(ownerId);

        final ScheduledTask[] holder = new ScheduledTask[1];
        ScheduledTask landingTask = owner.getScheduler().runAtFixedRate(plugin, task -> {
            holder[0] = task;
            if (!running
                    || !owner.isValid()
                    || owner.isDead()
                    || !isPrimaryDragon(owner)
                    || !activeLandings.contains(ownerId)) {
                cancelLandingWatcher(ownerId, task);
                return;
            }
            if (landingSpawnAttempted.contains(ownerId)) {
                cancelLandingWatcher(ownerId, task);
                return;
            }

            EnderDragon.Phase phase = owner.getPhase();
            if (!LANDING_ACTIVE_PHASES.contains(phase)) {
                activeLandings.remove(ownerId);
                landingSpawnAttempted.remove(ownerId);
                cancelLandingWatcher(ownerId, task);
                return;
            }

            Location ownerLocation = owner.getLocation().clone();
            Location podium = owner.getPodium().clone();
            boolean closeEnough = ownerLocation.getWorld() == podium.getWorld()
                    && ownerLocation.distanceSquared(podium) <= 24.0D * 24.0D;
            if (!closeEnough) {
                return;
            }

            landingSpawnAttempted.add(ownerId);
            cancelLandingWatcher(ownerId, task);
            trySpawnDragonling(owner, ownerLocation);
        }, () -> {
            ScheduledTask task = holder[0];
            if (task != null) {
                landingTasks.remove(ownerId, task);
            } else {
                landingTasks.remove(ownerId);
            }
        }, 5L, 5L);

        holder[0] = landingTask;
        if (landingTask != null) {
            landingTasks.put(ownerId, landingTask);
        }
    }

    private void cancelLandingWatcher(UUID ownerId) {
        ScheduledTask task = landingTasks.remove(ownerId);
        if (task != null) {
            task.cancel();
        }
    }

    private void cancelLandingWatcher(UUID ownerId, ScheduledTask expected) {
        landingTasks.remove(ownerId, expected);
        expected.cancel();
    }

    private void trySpawnDragonling(EnderDragon owner, Location ownerLocation) {
        UUID ownerId = owner.getUniqueId();
        int maximumActive = Math.max(0, plugin.getConfig().getInt(
                "ender-dragon-rework.dragonling.maximum-active", 2));
        if (retiredOwners.contains(ownerId)
                || maximumActive == 0
                || activeCount(ownerId) >= maximumActive) {
            return;
        }

        double angle = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
        Location spawnLocation = ownerLocation.clone().add(
                Math.cos(angle) * 5.0D,
                5.0D,
                Math.sin(angle) * 5.0D
        );
        Bukkit.getRegionScheduler().execute(plugin, spawnLocation, () -> spawnDragonling(spawnLocation, ownerId));
    }

    private void spawnDragonling(Location spawnLocation, UUID ownerId) {
        if (!running || spawnLocation.getWorld() == null) {
            return;
        }
        int maximumActive = Math.max(0, plugin.getConfig().getInt(
                "ender-dragon-rework.dragonling.maximum-active", 2));
        if (retiredOwners.contains(ownerId)
                || maximumActive == 0
                || activeCount(ownerId) >= maximumActive) {
            return;
        }

        Entity spawned = spawnLocation.getWorld().spawnEntity(spawnLocation, org.bukkit.entity.EntityType.ENDER_DRAGON);
        if (!(spawned instanceof EnderDragon dragonling)) {
            spawned.remove();
            return;
        }

        PersistentDataContainer pdc = dragonling.getPersistentDataContainer();
        pdc.set(dragonlingKey, PersistentDataType.BYTE, (byte) 1);
        pdc.set(ownerKey, PersistentDataType.STRING, ownerId.toString());

        double scale = clamp(plugin.getConfig().getDouble(
                "ender-dragon-rework.dragonling.scale", 0.25D), 0.0625D, 4.0D);
        double health = Math.max(1.0D, plugin.getConfig().getDouble(
                "ender-dragon-rework.dragonling.health", 40.0D));
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
            dragonling.remove();
            return;
        }
        controller.start();
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
            removeOnEntityThread(controller.dragon());
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

    private void removeOnEntityThread(EnderDragon dragonling) {
        dragonling.getScheduler().run(plugin, task -> {
            if (isDragonling(dragonling) && dragonling.isValid()) {
                dragonling.remove();
            }
        }, () -> { });
    }

    private void removeMarkedDragonlingsFromLoadedChunks() {
        for (World world : Bukkit.getWorlds()) {
            for (Chunk chunk : world.getLoadedChunks()) {
                Location anchor = new Location(
                        world,
                        (chunk.getX() << 4) + 8.0D,
                        world.getMinHeight() + 1.0D,
                        (chunk.getZ() << 4) + 8.0D
                );
                Bukkit.getRegionScheduler().execute(plugin, anchor, () -> {
                    if (!running) {
                        return;
                    }
                    for (Entity entity : chunk.getEntities()) {
                        if (entity instanceof EnderDragon dragon
                                && isDragonling(dragon)
                                && !controllers.containsKey(dragon.getUniqueId())) {
                            dragon.remove();
                        }
                    }
                });
            }
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

    private double clamp(double value, double minimum, double maximum) {
        return Math.max(minimum, Math.min(maximum, value));
    }
}
