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
import org.bukkit.boss.DragonBattle;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Phantom;
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

/** Spawns and manages purple phantom flocks owned by the primary Ender Dragon. */
public final class DragonPhantomManager {
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
    private final DragonPortalAbilities portalAbilities;
    private final NamespacedKey phantomKey = new NamespacedKey("taskforge", "dragon_phantom");
    private final NamespacedKey ownerKey = new NamespacedKey("taskforge", "dragon_phantom_owner");
    private final NamespacedKey legacyDragonlingKey = new NamespacedKey("taskforge", "dragonling");
    private final Map<UUID, DragonPhantomAttackController> controllers = new ConcurrentHashMap<>();
    private final Map<UUID, Set<UUID>> phantomsByOwner = new ConcurrentHashMap<>();
    private final Map<UUID, Integer> pendingSpawnsByOwner = new ConcurrentHashMap<>();
    private final Map<UUID, Object> ownerLocks = new ConcurrentHashMap<>();
    private final Map<UUID, PrimaryMonitor> primaryMonitors = new ConcurrentHashMap<>();
    private final Set<UUID> retiredOwners = ConcurrentHashMap.newKeySet();
    private final AtomicInteger startupScanTasks = new AtomicInteger();
    private final AtomicInteger startupPrimaryDragons = new AtomicInteger();
    private final AtomicInteger startupOrphanPhantoms = new AtomicInteger();
    private final AtomicInteger startupLegacyDragonlings = new AtomicInteger();
    private volatile boolean running;

    public DragonPhantomManager(CustomMobTweaksPlugin plugin, DragonPortalAbilities portalAbilities) {
        this.plugin = plugin;
        this.portalAbilities = portalAbilities;
    }

    public void start() {
        running = true;
        debug("purple appearance mode=particle-aura-only reason=Folia-scoreboard-api-unsupported");
        debug("manager start flockSize=" + configInt("flock-size-min", 5)
                + "-" + configInt("flock-size-max", 6)
                + " maximumActive=" + configInt("maximum-active", 12)
                + " size=" + configInt("size-min", 1) + "-" + configInt("size-max", 3)
                + " health=" + configDouble("health", 20.0D)
                + " landingMonitorPeriodTicks=" + configLong("landing-monitor-period-ticks", 5L)
                + " landingHorizontalRadius=" + configDouble("landing-horizontal-radius", 8.0D)
                + " landingVerticalRadius=" + configDouble("landing-vertical-radius", 16.0D)
                + " landingConfirmationSamples=" + configInt("landing-confirmation-samples", 2));
        inspectLoadedEntities();
        for (World world : Bukkit.getWorlds()) {
            inspectWorldBattle(world, "manager-start");
        }
    }

    public void shutdown() {
        running = false;

        for (PrimaryMonitor monitor : Set.copyOf(primaryMonitors.values())) {
            monitor.stop("shutdown");
        }
        primaryMonitors.clear();

        for (DragonPhantomAttackController controller : Set.copyOf(controllers.values())) {
            controller.stop();
            removeOnEntityThread(controller.phantom(), "shutdown");
        }
        controllers.clear();
        phantomsByOwner.clear();
        pendingSpawnsByOwner.clear();
        retiredOwners.clear();
        ownerLocks.clear();
        debug("manager stopped");
    }

    public boolean isDragonPhantom(Phantom phantom) {
        return phantom.getPersistentDataContainer().has(phantomKey, PersistentDataType.BYTE);
    }

    public boolean isLegacyDragonling(EnderDragon dragon) {
        return dragon.getPersistentDataContainer().has(legacyDragonlingKey, PersistentDataType.BYTE);
    }

    public void handleEntityAdded(Entity entity, String source) {
        if (!running) {
            return;
        }
        if (entity instanceof EnderDragon dragon) {
            handleDragonAdded(dragon, source);
        } else if (entity instanceof Phantom phantom && isDragonPhantom(phantom)) {
            handlePhantomAdded(phantom, source);
        }
    }

    public void handleEntitiesLoad(Collection<Entity> entities) {
        if (!running) {
            return;
        }
        for (Entity entity : entities) {
            handleEntityAdded(entity, "entities-load");
        }
    }

    /** Directly discovers the battle dragon without depending on chunk/entity load event ordering. */
    public void inspectWorldBattle(World world, String source) {
        if (!running || world == null || world.getEnvironment() != World.Environment.THE_END) {
            return;
        }
        Bukkit.getGlobalRegionScheduler().execute(plugin, () -> {
            if (!running) {
                return;
            }
            DragonBattle battle = world.getEnderDragonBattle();
            EnderDragon dragon = battle == null ? null : battle.getEnderDragon();
            if (dragon == null) {
                debug("world battle discovery source=" + source
                        + " world=" + world.getKey() + " result=no-active-dragon");
                return;
            }
            dragon.getScheduler().run(plugin,
                    task -> observePrimary(dragon, "world-battle:" + source),
                    () -> debug("world battle dragon retired before observation source=" + source
                            + " world=" + world.getKey()
                            + " uuid=" + dragon.getUniqueId()));
        });
    }

    /** Called from dragon-owned events, providing a second observer path for respawned dragons. */
    public void handlePrimaryActivity(EnderDragon dragon, String source) {
        if (!running || dragon == null) {
            return;
        }
        observePrimary(dragon, source);
    }

    /**
     * A vanilla EnderDragonFlameEvent is definitive proof that the primary dragon is perched and
     * performing its portal breath attack. It therefore acts as a hard landing confirmation if the
     * normal phase/proximity monitor was not attached in time.
     */
    public void handlePerchedBreath(EnderDragon dragon, Location flameLocation, String source) {
        if (!running || dragon == null || isLegacyDragonling(dragon)) {
            return;
        }
        observePrimary(dragon, source);
        PrimaryMonitor monitor = primaryMonitors.get(dragon.getUniqueId());
        if (monitor != null) {
            monitor.confirmFromPerchedBreath(flameLocation, source);
        }
    }

    public int observedPrimaryCount() {
        return primaryMonitors.size();
    }

    public int activePhantomCount() {
        return controllers.size();
    }

    public void handlePhaseChange(EnderDragonChangePhaseEvent event) {
        EnderDragon dragon = event.getEntity();
        if (isLegacyDragonling(dragon)) {
            event.setNewPhase(EnderDragon.Phase.CIRCLING);
            removeLegacyDragonling(dragon, "phase-event");
            return;
        }

        observePrimary(dragon, "phase-event");
        debug("primary phase event uuid=" + dragon.getUniqueId()
                + " old=" + event.getCurrentPhase()
                + " new=" + event.getNewPhase()
                + " cancelled=" + event.isCancelled()
                + " battlePresent=" + (dragon.getDragonBattle() != null)
                + " location=" + formatLocation(dragon.getLocation()));

        PrimaryMonitor monitor = primaryMonitors.get(dragon.getUniqueId());
        if (monitor != null) {
            monitor.processPhase(event.getNewPhase(), "phase-event");
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
        debug("primary death uuid=" + ownerId + " ownedPhantoms=" + count + " action=remove-owned");
        removeOwnedPhantoms(ownerId);
        portalAbilities.handlePrimaryDragonDeath(owner);
    }

    public void handlePhantomDeath(Phantom phantom) {
        UUID ownerId = ownerOf(phantom);
        debug("dragon phantom death uuid=" + phantom.getUniqueId() + " owner=" + ownerId);
        unregister(phantom.getUniqueId(), ownerId);
    }

    private void handleDragonAdded(EnderDragon dragon, String source) {
        dragon.getScheduler().runDelayed(plugin, task -> {
            if (!running || !dragon.isValid() || dragon.isDead()) {
                return;
            }
            if (isLegacyDragonling(dragon)) {
                removeLegacyDragonling(dragon, source);
                return;
            }
            observePrimary(dragon, source);
        }, () -> debug("dragon retired before add inspection source=" + source
                + " uuid=" + dragon.getUniqueId()), 1L);
    }

    private void handlePhantomAdded(Phantom phantom, String source) {
        phantom.getScheduler().runDelayed(plugin, task -> {
            if (!running || !phantom.isValid() || phantom.isDead() || !isDragonPhantom(phantom)) {
                return;
            }
            DragonPhantomAttackController controller = controllers.get(phantom.getUniqueId());
            if (controller == null) {
                debug("orphan dragon phantom detected source=" + source
                        + " uuid=" + phantom.getUniqueId() + " action=remove");
                phantom.setGlowing(false);
                phantom.remove();
            } else {
                preparePurpleAppearance(phantom);
                controller.start();
            }
        }, () -> debug("dragon phantom retired before add inspection source=" + source
                + " uuid=" + phantom.getUniqueId()), 1L);
    }

    private void observePrimary(EnderDragon dragon, String source) {
        if (!running || !dragon.isValid() || dragon.isDead() || isLegacyDragonling(dragon)) {
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
        portalAbilities.applyPrimaryHealth(dragon, source);
        debug("primary observed source=" + source
                + " uuid=" + dragon.getUniqueId()
                + " world=" + dragon.getWorld().getKey()
                + " phase=" + dragon.getPhase()
                + " battlePresent=" + (dragon.getDragonBattle() != null)
                + " podium=" + formatLocation(dragon.getPodium())
                + " location=" + formatLocation(dragon.getLocation()));
        created.start();
    }

    private void trySpawnFlock(EnderDragon owner, int landingCycle, String source, Location confirmedPortal) {
        UUID ownerId = owner.getUniqueId();
        if (!running || retiredOwners.contains(ownerId)) {
            debug("phantom flock spawn skipped owner=" + ownerId + " cycle=" + landingCycle
                    + " reason=" + (!running ? "manager-stopped" : "owner-retired"));
            return;
        }

        int maximumActive = Math.max(0, configInt("maximum-active", 12));
        int minimumFlock = Math.max(1, configInt("flock-size-min", 5));
        int maximumFlock = Math.max(minimumFlock, configInt("flock-size-max", 6));
        int requested = minimumFlock == maximumFlock
                ? minimumFlock
                : ThreadLocalRandom.current().nextInt(minimumFlock, maximumFlock + 1);
        Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
        int spawnCount;
        int activeBefore;
        int pendingBefore;
        synchronized (ownerLock) {
            activeBefore = activeCount(ownerId);
            pendingBefore = pendingSpawnsByOwner.getOrDefault(ownerId, 0);
            int available = Math.max(0, maximumActive - activeBefore - pendingBefore);
            spawnCount = Math.min(requested, available);
            if (spawnCount > 0) {
                pendingSpawnsByOwner.put(ownerId, pendingBefore + spawnCount);
            }
        }

        if (maximumActive == 0 || spawnCount <= 0) {
            debug("phantom flock spawn skipped owner=" + ownerId
                    + " cycle=" + landingCycle
                    + " reason=limit active=" + activeBefore
                    + " pending=" + pendingBefore
                    + " maximum=" + maximumActive
                    + " requested=" + requested);
            return;
        }

        Location ownerLocation = owner.getLocation().clone();
        Location portal = confirmedPortal != null
                && confirmedPortal.getWorld() == ownerLocation.getWorld()
                ? confirmedPortal.clone()
                : ownerLocation.clone();
        double baseAngle = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
        double spawnRadius = Math.max(2.0D, configDouble("spawn-radius", 7.0D));
        double spawnHeight = Math.max(3.0D, configDouble("spawn-height", 12.0D));
        double anchorHeight = Math.max(4.0D, configDouble("anchor-height", 18.0D));
        Location sharedAnchor = portal.clone().add(0.0D, anchorHeight, 0.0D);

        debug("phantom flock spawn dispatch owner=" + ownerId
                + " cycle=" + landingCycle
                + " source=" + source
                + " ownerLocation=" + formatLocation(ownerLocation)
                + " portal=" + formatLocation(portal)
                + " requested=" + requested
                + " spawning=" + spawnCount
                + " active=" + activeBefore + "/" + maximumActive);

        for (int index = 0; index < spawnCount; index++) {
            double angle = baseAngle + ((Math.PI * 2.0D) * index / spawnCount);
            double radius = spawnRadius + ThreadLocalRandom.current().nextDouble(-1.25D, 1.25D);
            double heightJitter = ThreadLocalRandom.current().nextDouble(-2.0D, 2.0D);
            Location spawnLocation = portal.clone().add(
                    Math.cos(angle) * radius,
                    spawnHeight + heightJitter,
                    Math.sin(angle) * radius
            );
            int memberIndex = index + 1;
            Bukkit.getRegionScheduler().execute(plugin, spawnLocation,
                    () -> spawnPhantom(spawnLocation, sharedAnchor, ownerId,
                            landingCycle, memberIndex, spawnCount, maximumActive));
        }
    }

    private void spawnPhantom(Location spawnLocation, Location anchor, UUID ownerId,
                              int landingCycle, int memberIndex, int flockSize, int maximumActive) {
        Phantom phantom = null;
        try {
            if (!running || spawnLocation.getWorld() == null || retiredOwners.contains(ownerId)) {
                debug("dragon phantom spawn aborted owner=" + ownerId
                        + " cycle=" + landingCycle + " member=" + memberIndex + "/" + flockSize
                        + " reason=runtime-world-or-owner");
                return;
            }

            phantom = spawnLocation.getWorld().spawn(spawnLocation, Phantom.class, created -> {
                PersistentDataContainer pdc = created.getPersistentDataContainer();
                pdc.set(phantomKey, PersistentDataType.BYTE, (byte) 1);
                pdc.set(ownerKey, PersistentDataType.STRING, ownerId.toString());
            });

            int minimumSize = Math.max(0, configInt("size-min", 1));
            int maximumSize = Math.max(minimumSize, configInt("size-max", 3));
            int size = minimumSize == maximumSize
                    ? minimumSize
                    : ThreadLocalRandom.current().nextInt(minimumSize, maximumSize + 1);
            double health = Math.max(1.0D, configDouble("health", 20.0D));
            double damage = Math.max(0.0D, configDouble("attack-damage", 6.0D));
            double followRange = Math.max(8.0D, configDouble("target-range", 72.0D));

            phantom.setSize(size);
            setBaseAttribute(phantom, Attribute.MAX_HEALTH, health);
            setBaseAttribute(phantom, Attribute.ATTACK_DAMAGE, damage);
            setBaseAttribute(phantom, Attribute.FOLLOW_RANGE, followRange);
            phantom.setHealth(health);
            phantom.setShouldBurnInDay(false);
            phantom.setPersistent(true);
            phantom.setRemoveWhenFarAway(false);
            phantom.setAware(true);
            phantom.setAggressive(true);
            phantom.setCanPickupItems(false);
            phantom.clearLootTable();
            phantom.setCustomNameVisible(false);
            phantom.setAnchorLocation(anchor);
            phantom.setTarget(null);
            phantom.setGlowing(false);

            DragonPhantomAttackController controller = new DragonPhantomAttackController(plugin, phantom, anchor);
            Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
            synchronized (ownerLock) {
                if (!running || retiredOwners.contains(ownerId) || activeCount(ownerId) >= maximumActive) {
                    debug("dragon phantom registration lost race uuid=" + phantom.getUniqueId()
                            + " owner=" + ownerId + " action=remove");
                    phantom.remove();
                    return;
                }
                controllers.put(phantom.getUniqueId(), controller);
                phantomsByOwner.computeIfAbsent(ownerId, ignored -> ConcurrentHashMap.newKeySet())
                        .add(phantom.getUniqueId());
            }

            preparePurpleAppearance(phantom);
            debug("dragon phantom spawned uuid=" + phantom.getUniqueId()
                    + " owner=" + ownerId
                    + " cycle=" + landingCycle
                    + " member=" + memberIndex + "/" + flockSize
                    + " size=" + size
                    + " health=" + health
                    + " damage=" + damage
                    + " anchor=" + formatLocation(anchor)
                    + " location=" + formatLocation(phantom.getLocation())
                    + " activeNow=" + activeCount(ownerId) + "/" + maximumActive);
            controller.start();
        } catch (Throwable error) {
            if (phantom != null && phantom.isValid()) {
                phantom.remove();
            }
            plugin.getLogger().log(Level.SEVERE,
                    "[dragon][phantoms] spawn failed owner=" + ownerId
                            + " cycle=" + landingCycle
                            + " member=" + memberIndex + "/" + flockSize
                            + " location=" + formatLocation(spawnLocation), error);
        } finally {
            releasePendingSpawn(ownerId);
        }
    }

    private void releasePendingSpawn(UUID ownerId) {
        Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
        boolean idle;
        synchronized (ownerLock) {
            int pending = pendingSpawnsByOwner.getOrDefault(ownerId, 0);
            if (pending <= 1) {
                pendingSpawnsByOwner.remove(ownerId);
            } else {
                pendingSpawnsByOwner.put(ownerId, pending - 1);
            }
            idle = !pendingSpawnsByOwner.containsKey(ownerId) && !phantomsByOwner.containsKey(ownerId);
        }
        if (idle) {
            ownerLocks.remove(ownerId, ownerLock);
        }
    }

    private void preparePurpleAppearance(Phantom phantom) {
        // Folia explicitly considers the entire scoreboard API broken. A colored glowing
        // outline requires scoreboard teams, so dragon phantoms use a dense purple particle
        // aura instead. Keep the vanilla glowing flag disabled to avoid a white outline.
        phantom.setGlowing(false);
    }

    private void removeOwnedPhantoms(UUID ownerId) {
        Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
        Set<DragonPhantomAttackController> removedControllers = ConcurrentHashMap.newKeySet();
        synchronized (ownerLock) {
            Set<UUID> ids = phantomsByOwner.remove(ownerId);
            if (ids != null) {
                for (UUID phantomId : Set.copyOf(ids)) {
                    DragonPhantomAttackController controller = controllers.remove(phantomId);
                    if (controller != null) {
                        removedControllers.add(controller);
                    }
                }
            }
            pendingSpawnsByOwner.remove(ownerId);
        }
        for (DragonPhantomAttackController controller : removedControllers) {
            controller.stop();
            removeOnEntityThread(controller.phantom(), "primary-death");
        }
        ownerLocks.remove(ownerId, ownerLock);
    }

    private void unregister(UUID phantomId, UUID ownerId) {
        DragonPhantomAttackController controller;
        if (ownerId == null) {
            controller = controllers.remove(phantomId);
        } else {
            Object ownerLock = ownerLocks.computeIfAbsent(ownerId, ignored -> new Object());
            synchronized (ownerLock) {
                controller = controllers.remove(phantomId);
                Set<UUID> ids = phantomsByOwner.get(ownerId);
                if (ids != null) {
                    ids.remove(phantomId);
                    if (ids.isEmpty()) {
                        phantomsByOwner.remove(ownerId, ids);
                    }
                }
            }
            if (!phantomsByOwner.containsKey(ownerId) && !pendingSpawnsByOwner.containsKey(ownerId)) {
                ownerLocks.remove(ownerId, ownerLock);
            }
        }
        if (controller != null) {
            controller.stop();
        }
    }

    private UUID ownerOf(Phantom phantom) {
        String raw = phantom.getPersistentDataContainer().get(ownerKey, PersistentDataType.STRING);
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
        Set<UUID> ids = phantomsByOwner.get(ownerId);
        return ids == null ? 0 : ids.size();
    }

    private void removeOnEntityThread(Phantom phantom, String reason) {
        phantom.getScheduler().run(plugin, task -> {
            if (isDragonPhantom(phantom) && phantom.isValid()) {
                debug("dragon phantom remove uuid=" + phantom.getUniqueId() + " reason=" + reason);
                phantom.setGlowing(false);
                phantom.remove();
            }
        }, null);
    }

    private void removeLegacyDragonling(EnderDragon dragon, String source) {
        debug("legacy dragonling cleanup source=" + source
                + " uuid=" + dragon.getUniqueId() + " action=remove");
        if (dragon.isValid()) {
            dragon.remove();
        }
    }

    private void inspectLoadedEntities() {
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
                            if (entity instanceof EnderDragon dragon) {
                                if (isLegacyDragonling(dragon)) {
                                    startupLegacyDragonlings.incrementAndGet();
                                    removeLegacyDragonling(dragon, "startup-loaded-chunk");
                                } else {
                                    observePrimary(dragon, "startup-loaded-chunk");
                                }
                            } else if (entity instanceof Phantom phantom && isDragonPhantom(phantom)) {
                                if (!controllers.containsKey(phantom.getUniqueId())) {
                                    startupOrphanPhantoms.incrementAndGet();
                                    debug("startup orphan dragon phantom uuid=" + phantom.getUniqueId()
                                            + " action=remove");
                                    phantom.setGlowing(false);
                                    phantom.remove();
                                }
                            }
                        }
                    } finally {
                        if (startupScanTasks.decrementAndGet() == 0) {
                            debug("startup loaded-chunk inspection complete primaryDragons="
                                    + startupPrimaryDragons.get()
                                    + " orphanDragonPhantomsRemoved=" + startupOrphanPhantoms.get()
                                    + " legacyDragonlingsRemoved=" + startupLegacyDragonlings.get()
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
            debug("startup loaded-chunk inspection complete primaryDragons=0 orphanDragonPhantomsRemoved=0"
                    + " legacyDragonlingsRemoved=0 hint=no-loaded-chunks; entity-add/load events remain armed");
        }
    }

    private Location resolvePortalAnchor(EnderDragon dragon) {
        Location dragonLocation = dragon.getLocation().clone();
        DragonBattle battle = dragon.getDragonBattle();
        if (battle != null) {
            Location portal = battle.getEndPortalLocation();
            if (portal != null && portal.getWorld() == dragonLocation.getWorld()) {
                return portal.clone();
            }
        }

        Location podium = dragon.getPodium();
        if (podium != null && podium.getWorld() == dragonLocation.getWorld()) {
            double y = podium.getY();
            if (y <= dragonLocation.getWorld().getMinHeight() + 1.0D) {
                y = dragonLocation.getY() - 4.0D;
            }
            return new Location(dragonLocation.getWorld(), podium.getX(), y, podium.getZ());
        }
        return null;
    }

    private double horizontalDistance(Location first, Location second) {
        double dx = first.getX() - second.getX();
        double dz = first.getZ() - second.getZ();
        return Math.sqrt(dx * dx + dz * dz);
    }

    private void setBaseAttribute(Phantom phantom, Attribute attribute, double value) {
        AttributeInstance instance = phantom.getAttribute(attribute);
        if (instance == null) {
            phantom.registerAttribute(attribute);
            instance = phantom.getAttribute(attribute);
        }
        if (instance == null) {
            throw new IllegalStateException("Phantom is missing required attribute " + attribute.getKey());
        }
        instance.setBaseValue(value);
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("ender-dragon-rework.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[dragon][phantoms] " + message);
        }
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

    private String formatLocation(Location location) {
        if (location == null) {
            return "null";
        }
        return String.format(java.util.Locale.ROOT, "%.2f,%.2f,%.2f",
                location.getX(), location.getY(), location.getZ());
    }

    private final class PrimaryMonitor {
        private final EnderDragon dragon;
        private volatile ScheduledTask task;
        private EnderDragon.Phase lastPhase;
        private boolean landingCycleActive;
        private boolean spawnAttempted;
        private boolean approachLogged;
        private boolean landingConfirmed;
        private boolean postLandingAbilitiesTriggered;
        private Location confirmedPortal;
        private int nearPortalSamples;
        private int lastProximityTick = Integer.MIN_VALUE;
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
                if (!running || !dragon.isValid() || dragon.isDead() || isLegacyDragonling(dragon)) {
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
            if (!running || !dragon.isValid() || dragon.isDead() || isLegacyDragonling(dragon)) {
                return;
            }
            if (LANDING_FAMILY.contains(phase)) {
                if (!landingCycleActive) {
                    beginLandingCycle(phase, source);
                }

                if (LANDED_PHASES.contains(phase) && !spawnAttempted) {
                    Location dragonLocation = dragon.getLocation().clone();
                    Location portal = resolvePortalAnchor(dragon);
                    if (portal == null || portal.getWorld() != dragonLocation.getWorld()) {
                        if (!approachLogged) {
                            approachLogged = true;
                            debug("landing waiting owner=" + dragon.getUniqueId()
                                    + " cycle=" + landingCycle
                                    + " reason=portal-unresolved"
                                    + " phase=" + phase
                                    + " location=" + formatLocation(dragonLocation));
                        }
                        return;
                    }

                    double horizontalDistance = horizontalDistance(dragonLocation, portal);
                    double verticalDistance = Math.abs(dragonLocation.getY() - portal.getY());
                    double horizontalRadius = Math.max(1.0D,
                            configDouble("landing-horizontal-radius", 8.0D));
                    double verticalRadius = Math.max(2.0D,
                            configDouble("landing-vertical-radius", 16.0D));
                    boolean physicallySeated = horizontalDistance <= horizontalRadius
                            && verticalDistance <= verticalRadius;

                    if (!physicallySeated) {
                        nearPortalSamples = 0;
                        lastProximityTick = Integer.MIN_VALUE;
                        if (!approachLogged) {
                            approachLogged = true;
                            debug("landing approach owner=" + dragon.getUniqueId()
                                    + " cycle=" + landingCycle
                                    + " phase=" + phase
                                    + " horizontalDistance=" + String.format(java.util.Locale.ROOT, "%.2f", horizontalDistance)
                                    + " verticalDistance=" + String.format(java.util.Locale.ROOT, "%.2f", verticalDistance)
                                    + " requiredHorizontal<=" + horizontalRadius
                                    + " requiredVertical<=" + verticalRadius
                                    + " action=wait-for-real-portal-arrival");
                        }
                        return;
                    }

                    int currentTick = dragon.getTicksLived();
                    if (currentTick != lastProximityTick) {
                        lastProximityTick = currentTick;
                        nearPortalSamples++;
                    }
                    int requiredSamples = Math.max(1, configInt("landing-confirmation-samples", 2));
                    if (nearPortalSamples == 1) {
                        debug("landing proximity acquired owner=" + dragon.getUniqueId()
                                + " cycle=" + landingCycle
                                + " phase=" + phase
                                + " portal=" + formatLocation(portal)
                                + " location=" + formatLocation(dragonLocation)
                                + " samples=1/" + requiredSamples);
                    }
                    if (nearPortalSamples < requiredSamples) {
                        return;
                    }

                    spawnAttempted = true;
                    landingConfirmed = true;
                    confirmedPortal = portal.clone();
                    debug("landing confirmed owner=" + dragon.getUniqueId()
                            + " cycle=" + landingCycle
                            + " source=" + source
                            + " phase=" + phase
                            + " portal=" + formatLocation(portal)
                            + " location=" + formatLocation(dragonLocation)
                            + " horizontalDistance=" + String.format(java.util.Locale.ROOT, "%.2f", horizontalDistance)
                            + " verticalDistance=" + String.format(java.util.Locale.ROOT, "%.2f", verticalDistance)
                            + " samples=" + nearPortalSamples + "/" + requiredSamples
                            + " action=spawn-phantom-flock-and-arm-post-landing-abilities");
                    trySpawnFlock(dragon, landingCycle, source, portal);
                }
                return;
            }

            if (landingCycleActive) {
                if (landingConfirmed && !postLandingAbilitiesTriggered) {
                    postLandingAbilitiesTriggered = true;
                    Location portal = confirmedPortal != null ? confirmedPortal.clone() : resolvePortalAnchor(dragon);
                    portalAbilities.triggerAfterLanding(dragon, portal, landingCycle, source + ":" + phase);
                }
                debug("landing cycle end owner=" + dragon.getUniqueId()
                        + " cycle=" + landingCycle
                        + " source=" + source
                        + " phase=" + phase
                        + " spawnAttempted=" + spawnAttempted
                        + " landingConfirmed=" + landingConfirmed
                        + " postLandingAbilitiesTriggered=" + postLandingAbilitiesTriggered
                        + " nearPortalSamples=" + nearPortalSamples);
            }
            landingCycleActive = false;
            spawnAttempted = false;
            approachLogged = false;
            landingConfirmed = false;
            postLandingAbilitiesTriggered = false;
            confirmedPortal = null;
            nearPortalSamples = 0;
            lastProximityTick = Integer.MIN_VALUE;
        }

        private void beginLandingCycle(EnderDragon.Phase phase, String source) {
            landingCycleActive = true;
            spawnAttempted = false;
            approachLogged = false;
            landingConfirmed = false;
            postLandingAbilitiesTriggered = false;
            confirmedPortal = null;
            nearPortalSamples = 0;
            lastProximityTick = Integer.MIN_VALUE;
            landingCycle++;
            debug("landing cycle start owner=" + dragon.getUniqueId()
                    + " cycle=" + landingCycle
                    + " source=" + source
                    + " phase=" + phase
                    + " rawPodium=" + formatLocation(dragon.getPodium())
                    + " resolvedPortal=" + formatLocation(resolvePortalAnchor(dragon))
                    + " location=" + formatLocation(dragon.getLocation()));
        }

        private synchronized void confirmFromPerchedBreath(Location flameLocation, String source) {
            if (!running || !dragon.isValid() || dragon.isDead() || isLegacyDragonling(dragon)) {
                return;
            }
            EnderDragon.Phase phase = dragon.getPhase();
            if (!landingCycleActive) {
                beginLandingCycle(phase, source + ":implicit-cycle");
            }
            if (spawnAttempted) {
                debug("perched breath landing confirmation already satisfied owner=" + dragon.getUniqueId()
                        + " cycle=" + landingCycle
                        + " source=" + source
                        + " phase=" + phase);
                return;
            }

            Location portal = resolvePortalAnchor(dragon);
            if (portal == null || portal.getWorld() != dragon.getWorld()) {
                debug("perched breath could not confirm landing owner=" + dragon.getUniqueId()
                        + " cycle=" + landingCycle
                        + " source=" + source
                        + " reason=portal-unresolved"
                        + " flameLocation=" + formatLocation(flameLocation)
                        + " dragonLocation=" + formatLocation(dragon.getLocation()));
                return;
            }

            Location dragonLocation = dragon.getLocation().clone();
            spawnAttempted = true;
            landingConfirmed = true;
            confirmedPortal = portal.clone();
            nearPortalSamples = Math.max(nearPortalSamples,
                    Math.max(1, configInt("landing-confirmation-samples", 2)));
            debug("landing force-confirmed by perched breath owner=" + dragon.getUniqueId()
                    + " cycle=" + landingCycle
                    + " source=" + source
                    + " phase=" + phase
                    + " portal=" + formatLocation(portal)
                    + " flameLocation=" + formatLocation(flameLocation)
                    + " dragonLocation=" + formatLocation(dragonLocation)
                    + " horizontalDistance=" + String.format(java.util.Locale.ROOT, "%.2f",
                    horizontalDistance(dragonLocation, portal))
                    + " verticalDistance=" + String.format(java.util.Locale.ROOT, "%.2f",
                    Math.abs(dragonLocation.getY() - portal.getY()))
                    + " action=spawn-phantom-flock-and-arm-post-landing-abilities");
            trySpawnFlock(dragon, landingCycle, source, portal);
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
