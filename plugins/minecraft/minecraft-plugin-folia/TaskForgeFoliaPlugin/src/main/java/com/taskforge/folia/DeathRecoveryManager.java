package com.taskforge.folia;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.Optional;
import java.util.Properties;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.Consumer;
import java.util.logging.Level;

import com.google.gson.Gson;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.event.ClickEvent;
import net.kyori.adventure.text.format.NamedTextColor;
import net.kyori.adventure.title.Title;
import org.bukkit.Bukkit;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.block.BlockFace;
import org.bukkit.block.BlockState;
import org.bukkit.block.Container;
import org.bukkit.block.data.type.Chest;
import org.bukkit.command.Command;
import org.bukkit.command.CommandExecutor;
import org.bukkit.command.CommandSender;
import org.bukkit.entity.Item;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.PlayerDeathEvent;
import org.bukkit.event.player.PlayerChangedWorldEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerPortalEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import com.destroystokyo.paper.event.player.PlayerPostRespawnEvent;
import org.bukkit.event.player.PlayerTeleportEvent;
import org.bukkit.inventory.Inventory;
import org.bukkit.inventory.ItemStack;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.util.Vector;
import org.bukkit.util.io.BukkitObjectInputStream;
import org.bukkit.util.io.BukkitObjectOutputStream;

/**
 * Folia-safe, crash-resilient death offer manager.
 *
 * The backend is the shared source of truth while an atomic local journal is retained so that
 * drops are not lost when the rating/backend service is unavailable during a death or restart.
 */
public final class DeathRecoveryManager implements Listener, CommandExecutor {
    private static final Gson GSON = new Gson();
    private static final String API_PATH = "/api/integrations/minecraft/death-recovery";
    private static final String ACTION_COORDINATES = "coordinates";
    private static final String ACTION_CHEST = "chest";
    private static final String ACTION_RETURN = "return";
    private static final String ACTION_BOTH = "both";
    private static final String ACTION_DROP = "drop";
    private static final Duration OFFER_DISPLAY_COOLDOWN = Duration.ofSeconds(30);

    private final TaskForgeLinkPlugin plugin;
    private final HttpClient http;
    private final Path journalDir;
    private final Map<UUID, DeathRecord> deaths = new ConcurrentHashMap<>();
    private final Map<UUID, RescueSession> rescues = new ConcurrentHashMap<>();
    private final Map<UUID, PendingVanillaDeathNotice> vanillaDeathNotices = new ConcurrentHashMap<>();
    private final NamespacedKey deathIdKey;
    private final NamespacedKey itemIndexKey;
    private final NamespacedKey chestStateKey;
    private final AtomicBoolean stopped = new AtomicBoolean(false);
    private final AtomicLong backendRequestSequence = new AtomicLong();
    private volatile ScheduledTask heartbeatTask;

    private final int offerSeconds;
    private final int spectatorSeconds;
    private final int maxDistance;
    private final int chestSearchRadius;
    private final int emergencyChestSearchRadius;
    private final int safeSearchRadius;
    private final int coordinatesCost;
    private final int chestCost;
    private final int teleportCost;
    private final Duration backendTimeout;
    private final String apiBaseUrl;
    private final String serverToken;

    public DeathRecoveryManager(TaskForgeLinkPlugin plugin) {
        this.plugin = Objects.requireNonNull(plugin, "plugin");
        this.offerSeconds = positiveInt("deathRecovery.offerSeconds", 300);
        this.spectatorSeconds = positiveInt("deathRecovery.spectatorSeconds", 5);
        this.maxDistance = positiveInt("deathRecovery.maxDistance", 12);
        this.chestSearchRadius = positiveInt("deathRecovery.chestSearchRadius", 16);
        this.emergencyChestSearchRadius = Math.max(chestSearchRadius, positiveInt("deathRecovery.emergencyChestSearchRadius", 256));
        this.safeSearchRadius = positiveInt("deathRecovery.safeSearchRadius", 8);
        this.coordinatesCost = positiveInt("deathRecovery.coordinatesCost", 10);
        this.chestCost = positiveInt("deathRecovery.chestCost", 50);
        this.teleportCost = positiveInt("deathRecovery.teleportCost", 100);
        this.backendTimeout = Duration.ofMillis(positiveInt("deathRecovery.backendTimeoutMillis", 4500));
        this.apiBaseUrl = discoverString(
            "deathRecovery.apiBaseUrl", "taskforge.apiBaseUrl", "taskforge.apibaseUrl",
            "api.baseUrl", "backend.baseUrl", "backendUrl", "apiUrl");
        this.serverToken = discoverString(
            "deathRecovery.serverToken", "taskforge.pluginKey", "taskforge.pluginkey",
            "api.serverToken", "serverToken", "minecraft.serverToken");
        this.http = HttpClient.newBuilder().connectTimeout(backendTimeout).build();
        this.journalDir = plugin.getDataFolder().toPath().resolve("death-recovery");
        this.deathIdKey = new NamespacedKey(plugin, "death_id");
        this.itemIndexKey = new NamespacedKey(plugin, "death_item_index");
        this.chestStateKey = new NamespacedKey(plugin, "death_chest_state");
        plugin.debugDeath("manager constructed apiBaseUrl=" + (apiBaseUrl.isBlank() ? "<missing>" : apiBaseUrl)
            + " serverTokenPresent=" + !serverToken.isBlank() + " tokenLen=" + serverToken.length()
            + " offerSeconds=" + offerSeconds + " spectatorSeconds=" + spectatorSeconds
            + " maxDistance=" + maxDistance + " chestSearchRadius=" + chestSearchRadius
            + " emergencyChestSearchRadius=" + emergencyChestSearchRadius
            + " safeSearchRadius=" + safeSearchRadius + " backendTimeoutMs=" + backendTimeout.toMillis());
    }

    public void enable() {
        try {
            Files.createDirectories(journalDir);
            loadJournal();
            plugin.debugJournal("journal initialized dir=" + journalDir + " records=" + deaths.size());
        } catch (IOException ex) {
            plugin.getLogger().log(Level.SEVERE, "Cannot initialize death recovery journal", ex);
        }
        Bukkit.getPluginManager().registerEvents(this, plugin);
        if (plugin.getCommand("tfdeath") != null) {
            plugin.getCommand("tfdeath").setExecutor(this);
        } else {
            plugin.getLogger().severe("Command tfdeath is missing from plugin.yml; death offer buttons cannot work");
        }
        heartbeatTask = Bukkit.getGlobalRegionScheduler().runAtFixedRate(plugin, task -> heartbeat(), 20L, 20L);
        plugin.debugDeath("listeners registered; heartbeat scheduled every 20 ticks; loadedRecords=" + deaths.size());
        // Reconcile journal records with the backend without delaying plugin enable.  Older
        // builds may have captured a death for a player who was not actually linked.  Treat
        // an authoritative backend rejection exactly like the live-death path: release the
        // stored items instead of leaving the record stuck until the next cache refresh.
        for (DeathRecord record : deaths.values()) {
            syncCreate(record).thenAccept(result -> {
                plugin.debugDeath("startup backend reconciliation deathId=" + record.deathId
                    + " player=" + record.playerId + " stage=" + record.stage + " result=" + result);
                if (result == BackendResult.UNLINKED) {
                    plugin.markLinkStateUnlinked(record.playerId, "startup-death-state-put");
                    handleBackendUnlinked(record, "startup-backend-rejected-not-linked");
                } else if (result == BackendResult.UNAVAILABLE) {
                    plugin.debugDeath("startup backend reconciliation deferred deathId=" + record.deathId
                        + " reason=backend-unavailable; local journal remains authoritative");
                }
            });
        }

        // A hot plugin reload does not emit PlayerJoinEvent for players that are already online.
        // Resume offers, paid rescues and pending notifications explicitly in that case.
        for (Player player : plugin.onlinePlayersSnapshot()) {
            Set<UUID> knownBeforePull = deathIdsFor(player.getUniqueId());
            onPlayer(player, () -> {
                restoreInterruptedSpectator(player);
                showPendingOffers(player);
                notifyUnseenChests(player);
                resumeUnresolved(player);
                resumePaidRescues(player);
            }, 20L);
            pullPending(player).whenComplete((ignored, error) -> onPlayer(player, () -> {
                showPendingOffersExcluding(player, knownBeforePull);
                notifyUnseenChests(player);
                resumeUnresolved(player);
                resumePaidRescues(player);
            }, 1L));
        }
    }

    public void disable() {
        if (!stopped.compareAndSet(false, true)) {
            return;
        }
        ScheduledTask task = heartbeatTask;
        heartbeatTask = null;
        if (task != null) {
            try {
                task.cancel();
            } catch (Exception ex) {
                plugin.getLogger().log(Level.WARNING, "Cannot cancel death-recovery heartbeat", ex);
            }
        }
        HandlerList.unregisterAll(this);
        plugin.debugDeath("listeners unregistered; heartbeat cancelled; preparing journal for shutdown/reload");
        for (RescueSession session : new ArrayList<>(rescues.values())) {
            session.record.rescuePending = true;
            session.record.rescueCompleted = false;
            session.record.stage = Stage.RESCUE_PENDING;
            session.record.rescueEndsAt = Instant.now().plusSeconds(Math.max(1, session.secondsRemaining()));
            saveQuietly(session.record);
            Player player = plugin.findOnlinePlayer(session.record.playerId);
            if (player != null && player.isOnline()) {
                // The scheduler may be cancelled during shutdown, therefore the persisted
                // RESCUE_PENDING state is also repaired on the next enable/join.
                onPlayer(player, () -> restoreGameMode(player, session.record), 1L);
            }
        }
        rescues.clear();
        deaths.values().forEach(this::saveQuietly);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onDeath(PlayerDeathEvent event) {
        if (!plugin.getConfig().getBoolean("deathRecovery.enabled", true)) {
            plugin.debugDeath("death ignored because feature is disabled");
            return;
        }
        Player player = event.getEntity();
        Location rawDeathLocation = player.getLocation().clone();
        String rawWorld = rawDeathLocation.getWorld() == null ? "<null>" : rawDeathLocation.getWorld().getKey().toString();
        TaskForgeLinkPlugin.LinkState linkState = plugin.linkState(player.getUniqueId());
        plugin.debugDeath("PlayerDeathEvent player=" + player.getName() + " uuid=" + player.getUniqueId()
            + " keepInventory=" + event.getKeepInventory() + " rawDrops=" + event.getDrops().size()
            + " droppedExp=" + event.getDroppedExp() + " world=" + rawWorld
            + " xyz=" + rawDeathLocation.getX() + "," + rawDeathLocation.getY() + "," + rawDeathLocation.getZ()
            + " exempt=" + plugin.isExempt(player) + " " + plugin.linkStateDebug(player.getUniqueId()));
        if (plugin.isExempt(player)) {
            plugin.debugDeath("death left vanilla because player is exempt uuid=" + player.getUniqueId()
                + " dropsUntouched=" + event.getDrops().size());
            return;
        }
        if (linkState != TaskForgeLinkPlugin.LinkState.LINKED) {
            queueVanillaDeathNotice(player, linkState, event.getDrops().size(), rawDeathLocation,
                linkState == TaskForgeLinkPlugin.LinkState.UNLINKED ? "unlinked-cache" : "unknown-cache");
            plugin.debugDeath("death left completely vanilla because TaskForge link is not confirmed player="
                + player.getName() + "/" + player.getUniqueId() + " linkState=" + linkState
                + " dropsUntouched=" + event.getDrops().size() + " keepInventory=" + event.getKeepInventory()
                + " expUntouched=" + event.getDroppedExp());
            return;
        }
        // Another plugin/game rule owns the inventory lifecycle in this case. Capturing the same
        // stacks here would create a second copy in a later death chest.
        if (event.getKeepInventory()) {
            plugin.debugDeath("death capture skipped because keepInventory=true player=" + player.getUniqueId());
            return;
        }
        List<ItemStack> drops = new ArrayList<>();
        for (ItemStack item : event.getDrops()) {
            if (item != null && !item.getType().isAir() && item.getAmount() > 0) drops.add(item.clone());
        }
        if (drops.isEmpty()) {
            plugin.debugDeath("death capture skipped because final drop list is empty player=" + player.getUniqueId());
            return;
        }
        // Serialize before clearing so an unexpected serialization failure leaves vanilla drops intact.
        final String serializedDrops;
        try { serializedDrops = serializeItems(drops); }
        catch (RuntimeException ex) { plugin.getLogger().log(Level.SEVERE, "Cannot capture death drops", ex); return; }
        // XP and vanilla Curse of Vanishing behavior are already reflected by getDrops().
        Location at = player.getLocation().clone();
        World world = at.getWorld();
        if (world == null) {
            plugin.getLogger().severe("Death location has no world for " + player.getUniqueId());
            return;
        }
        DeathRecord record = new DeathRecord();
        record.deathId = UUID.randomUUID();
        record.playerId = player.getUniqueId();
        record.playerName = player.getName();
        record.worldUuid = world.getUID();
        record.worldKey = world.getKey().toString();
        record.worldName = world.getName();
        record.x = at.getX(); record.y = at.getY(); record.z = at.getZ();
        record.yaw = at.getYaw(); record.pitch = at.getPitch();
        record.itemsBase64 = serializedDrops;
        record.createdAt = Instant.now();
        record.offerExpiresAt = record.createdAt.plusSeconds(offerSeconds);
        record.stage = Stage.WAITING_RESPAWN;
        record.requestId = UUID.randomUUID();
        record.updatedAt = record.createdAt;
        // Persist the payload before suppressing the vanilla drop. If the journal cannot be written,
        // leave PlayerDeathEvent#getDrops() untouched so Minecraft remains the safe fallback.
        if (!persistLocal(record)) {
            plugin.getLogger().severe("Death drops were not intercepted because the local journal could not be written for " + player.getUniqueId());
            return;
        }
        deaths.put(record.deathId, record);
        int interceptedCount = event.getDrops().size();
        event.getDrops().clear();
        plugin.debugDeath("vanilla drops suppressed deathId=" + record.deathId + " player=" + record.playerId
            + " before=" + interceptedCount + " after=" + event.getDrops().size()
            + " reason=confirmed-linked-local-journal-persisted");
        plugin.debugDeath("captured deathId=" + record.deathId + " requestId=" + record.requestId
            + " player=" + record.playerName + "/" + record.playerId + " items=" + drops.size()
            + " itemSummary=" + summarizeItems(drops)
            + " worldUuid=" + record.worldUuid + " worldKey=" + record.worldKey + " worldName=" + record.worldName
            + " xyz=" + record.x + "," + record.y + "," + record.z + " offerExpiresAt=" + record.offerExpiresAt);
        syncCreate(record).thenAccept(result -> {
            plugin.debugDeath("initial backend synchronization result deathId=" + record.deathId
                + " result=" + result + " " + plugin.linkStateDebug(record.playerId));
            if (result == BackendResult.UNLINKED) {
                plugin.markLinkStateUnlinked(record.playerId, "death-state-put");
                handleBackendUnlinked(record, "backend-create-rejected-not-linked");
                return;
            }
            if (result == BackendResult.UNAVAILABLE) {
                fallbackUnclaimedOffer(record, "Сервис рейтинга временно недоступен.");
                return;
            }
            health(record.playerId).thenAccept(healthy -> {
                plugin.debugDeath("post-create health deathId=" + record.deathId + " available=" + healthy);
                if (!healthy) fallbackUnclaimedOffer(record, "Сервис рейтинга временно недоступен.");
            });
        });
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onPostRespawn(PlayerPostRespawnEvent event) {
        Player player = event.getPlayer();
        Set<UUID> knownBeforePull = deathIdsFor(player.getUniqueId());
        plugin.debugDeath("PlayerPostRespawnEvent player=" + player.getName() + " uuid=" + player.getUniqueId()
            + " localRecords=" + knownBeforePull.size() + " pendingVanillaNotice="
            + vanillaDeathNotices.containsKey(player.getUniqueId()) + " " + plugin.linkStateDebug(player.getUniqueId()));

        deliverVanillaDeathNotice(player, "post-respawn-event");

        // PlayerPostRespawnEvent is emitted after Paper/Folia has completed the respawn reset.
        // Queue one entity-owned task for the next tick so menu delivery stays on the player's region
        // without periodically polling all open deaths.
        onPlayer(player, () -> {
            restoreInterruptedSpectator(player);
            showPendingOffers(player);
            notifyUnseenChests(player);
            resumeUnresolved(player);
            resumePaidRescues(player);
        }, 1L);

        pullPending(player).whenComplete((ignored, error) -> {
            if (error != null) plugin.debugDeath("pending pull after post-respawn failed player=" + player.getUniqueId() + " error=" + error);
            onPlayer(player, () -> {
                showPendingOffersExcluding(player, knownBeforePull);
                notifyUnseenChests(player);
                resumeUnresolved(player);
                resumePaidRescues(player);
            }, 1L);
        });
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onJoin(PlayerJoinEvent event) {
        Player player = event.getPlayer();
        Set<UUID> knownBeforePull = deathIdsFor(player.getUniqueId());
        plugin.debugDeath("PlayerJoinEvent player=" + player.getName() + " uuid=" + player.getUniqueId()
            + " localRecords=" + knownBeforePull.size() + " pendingVanillaNotice="
            + vanillaDeathNotices.containsKey(player.getUniqueId()) + " " + plugin.linkStateDebug(player.getUniqueId()));

        deliverVanillaDeathNotice(player, "join-event");

        onPlayer(player, () -> {
            restoreInterruptedSpectator(player);
            showPendingOffers(player);
            notifyUnseenChests(player);
            resumeUnresolved(player);
            resumePaidRescues(player);
        }, 20L);

        pullPending(player).whenComplete((ignored, error) -> {
            if (error != null) plugin.debugDeath("pending pull after join failed player=" + player.getUniqueId() + " error=" + error);
            onPlayer(player, () -> {
                showPendingOffersExcluding(player, knownBeforePull);
                notifyUnseenChests(player);
                resumeUnresolved(player);
                resumePaidRescues(player);
            }, 1L);
        });
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onQuit(PlayerQuitEvent event) {
        UUID leavingPlayerId = event.getPlayer().getUniqueId();
        for (DeathRecord record : deaths.values()) {
            if (record.playerId.equals(leavingPlayerId)) record.lastOfferShownAt = Instant.EPOCH;
        }
        RescueSession session = rescues.remove(leavingPlayerId);
        if (session != null) {
            session.record.rescueEndsAt = Instant.now().plusSeconds(Math.max(1, session.secondsRemaining()));
            restoreGameMode(event.getPlayer(), session.record);
            saveQuietly(session.record);
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onPortal(PlayerPortalEvent event) {
        if (rescues.containsKey(event.getPlayer().getUniqueId())) {
            event.setCancelled(true);
            event.getPlayer().sendActionBar(Component.text("Во время выбора точки порталы отключены", NamedTextColor.RED));
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onTeleport(PlayerTeleportEvent event) {
        RescueSession session = rescues.get(event.getPlayer().getUniqueId());
        if (session == null || session.internalTeleport) return;
        World toWorld = event.getTo() == null ? null : event.getTo().getWorld();
        if (toWorld == null || !toWorld.getUID().equals(session.record.worldUuid)) {
            event.setCancelled(true);
            return;
        }
        if (event.getCause() == PlayerTeleportEvent.TeleportCause.NETHER_PORTAL
            || event.getCause() == PlayerTeleportEvent.TeleportCause.END_PORTAL) {
            event.setCancelled(true);
        }
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onChangedWorld(PlayerChangedWorldEvent event) {
        RescueSession session = rescues.get(event.getPlayer().getUniqueId());
        if (session != null && !event.getPlayer().getWorld().getUID().equals(session.record.worldUuid)) {
            Location center = deathLocation(session.record).orElse(null);
            if (center != null) teleportInternal(session, event.getPlayer(), center);
        }
    }

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        if (!(sender instanceof Player player)) return true;
        if (stopped.get()) {
            player.sendMessage(Component.text("TaskForgeLink сейчас применяет новый config.yml. Повторите действие через секунду.", NamedTextColor.YELLOW));
            return true;
        }
        if (args.length != 2) {
            player.sendMessage(Component.text("Эта команда используется кнопками предложения смерти.", NamedTextColor.YELLOW));
            return true;
        }
        UUID deathId;
        try { deathId = UUID.fromString(args[0]); }
        catch (IllegalArgumentException ex) { return true; }
        String action = args[1].toLowerCase(Locale.ROOT);
        if (!List.of(ACTION_COORDINATES, ACTION_CHEST, ACTION_RETURN, ACTION_BOTH, ACTION_DROP).contains(action)) return true;
        DeathRecord record = deaths.get(deathId);
        if (record == null || !record.playerId.equals(player.getUniqueId())) {
            player.sendMessage(Component.text("Предложение не найдено или уже завершено.", NamedTextColor.RED));
            return true;
        }
        choose(player, record, action);
        return true;
    }

    private void choose(Player player, DeathRecord record, String action) {
        TaskForgeLinkPlugin.LinkState currentLinkState = plugin.linkState(player.getUniqueId());
        plugin.debugDeath("death action requested player=" + player.getName() + "/" + player.getUniqueId()
            + " deathId=" + record.deathId + " action=" + action + " stage=" + record.stage
            + " offerClosed=" + record.offerClosed + " itemsResolved=" + record.itemsResolved
            + " coordinatesGranted=" + record.coordinatesGranted + " chestCreated=" + record.chestCreated
            + " rescueCompleted=" + record.rescueCompleted + " rescuePending=" + record.rescuePending
            + " inFlight=" + record.actionsInFlight + " " + plugin.linkStateDebug(player.getUniqueId()));
        if (currentLinkState != TaskForgeLinkPlugin.LinkState.LINKED) {
            player.sendMessage(Component.text("TaskForge не подтверждён. Вещи будут выпущены обычным способом.", NamedTextColor.YELLOW));
            handleBackendUnlinked(record, "action-click-link-state-" + currentLinkState.name().toLowerCase(Locale.ROOT));
            return;
        }
        synchronized (record) {
            if (record.offerClosed || Instant.now().isAfter(record.offerExpiresAt)) {
                player.sendMessage(Component.text("Время выбора уже истекло.", NamedTextColor.RED));
                expire(record);
                return;
            }
            String unavailable = actionUnavailableReason(record, action);
            if (unavailable != null) {
                player.sendMessage(Component.text(unavailable, NamedTextColor.YELLOW));
                showOffer(player, record, true, "unavailable-action");
                return;
            }
            if (!record.actionsInFlight.isEmpty() || record.chestSearchInFlight || record.compensationPending || record.rescuePending || rescues.containsKey(record.playerId)) {
                String active = activeOperationDescription(record);
                player.sendMessage(Component.text(
                    "Сейчас уже выполняется: " + active + ". Дождитесь результата, затем можно выбрать следующее действие.",
                    NamedTextColor.YELLOW));
                plugin.debugDeath("death action rejected because another operation is active deathId=" + record.deathId
                    + " requested=" + action + " active=" + active + " inFlight=" + record.actionsInFlight
                    + " chestSearch=" + record.chestSearchInFlight + " compensation=" + record.compensationPending
                    + " rescuePending=" + record.rescuePending + " rescueActive=" + rescues.containsKey(record.playerId));
                return;
            }
            record.actionsInFlight.add(action);
            record.action = action;
            record.stage = Stage.RESOLVING;
            record.updatedAt = Instant.now();
            deferRetry(record);
            saveQuietly(record);
        }

        player.sendMessage(Component.text(actionStartMessage(action, record), NamedTextColor.AQUA));
        switch (action) {
            case ACTION_COORDINATES -> purchaseCoordinates(player, record);
            case ACTION_DROP -> {
                health(record.playerId).thenAccept(healthy -> {
                    if (healthy) {
                        releaseDrops(record, null, () -> {
                            completeAction(record, ACTION_DROP);
                            onPlayer(player, () -> {
                                player.sendMessage(Component.text(
                                    "Обычный дроп выполнен: вещи выброшены в точке смерти. Координаты и возврат всё ещё доступны до конца времени выбора.",
                                    NamedTextColor.GREEN));
                                showOffer(player, record, true, "drop-completed");
                            }, 1L);
                        });
                    } else {
                        completeAction(record, ACTION_DROP);
                        createFreeChest(record, "Сервис рейтинга недоступен — вместо дропа вещи бесплатно сохранены в сундуке.");
                    }
                });
            }
            case ACTION_CHEST -> prepareChestAndPurchase(player, record, false);
            case ACTION_RETURN -> purchaseAndReturn(player, record);
            case ACTION_BOTH -> prepareBoth(player, record);
            default -> completeAction(record, action);
        }
    }


    private String actionStartMessage(String action, DeathRecord record) {
        return switch (action) {
            case ACTION_COORDINATES -> "Действие запущено: координаты смерти. Стоимость — " + coordinatesCost + ". Проверяю баланс.";
            case ACTION_CHEST -> "Действие запущено: сундук. Стоимость — " + chestCost + ". Ищу ближайшее свободное место и проверяю баланс.";
            case ACTION_RETURN -> "Действие запущено: возврат к месту смерти. Стоимость — " + teleportCost + ". Вещи будут выброшены в точке смерти, затем начнётся возврат.";
            case ACTION_BOTH -> {
                int cost = remainingBundleCost(record);
                yield "Действие запущено: " + remainingBundleTitle(record) + ". Стоимость — " + cost + ". Проверяю место, баланс и безопасную точку.";
            }
            case ACTION_DROP -> "Действие запущено: обычный дроп. Стоимость — 0. Вещи будут выброшены в точке смерти.";
            default -> "Действие запущено.";
        };
    }

    private String activeOperationDescription(DeathRecord record) {
        if (record.compensationPending) return "возврат ошибочного списания";
        if (record.rescuePending || rescues.containsKey(record.playerId)) return "возврат к месту смерти";
        if (record.chestSearchInFlight) return "поиск места и создание сундука";
        String active = record.actionsInFlight.stream().findFirst().orElse(record.action);
        return switch (active == null ? "" : active) {
            case ACTION_COORDINATES -> "получение координат";
            case ACTION_CHEST -> "создание сундука";
            case ACTION_RETURN -> "возврат к месту смерти";
            case ACTION_BOTH -> "создание сундука и возврат";
            case ACTION_DROP -> "обычный дроп";
            default -> "предыдущее действие";
        };
    }

    private int remainingBundleCost(DeathRecord record) {
        int cost = 0;
        if (!record.chestCreated && !record.chestPurchased && !record.itemsResolved) cost += chestCost;
        if (!record.returnPurchased && !record.rescueCompleted && !record.rescuePending && !rescues.containsKey(record.playerId)) cost += teleportCost;
        return cost;
    }

    private String remainingBundleTitle(DeathRecord record) {
        boolean chest = !record.chestCreated && !record.chestPurchased && !record.itemsResolved;
        boolean back = !record.returnPurchased && !record.rescueCompleted && !record.rescuePending && !rescues.containsKey(record.playerId);
        if (chest && back) return "сундук и возврат";
        if (chest) return "сундук";
        if (back) return "возврат";
        return "оставшиеся действия";
    }

    private String actionUnavailableReason(DeathRecord record, String action) {
        if (ACTION_COORDINATES.equals(action) && record.coordinatesGranted)
            return "Координаты для этой смерти уже получены.";
        if (ACTION_CHEST.equals(action)) {
            if (record.chestCreated) return "Сундук для этой смерти уже создан.";
            if (record.chestPurchased) return "Сундук уже оплачен и сейчас создаётся. Повторного списания не будет.";
            if (record.itemsResolved) return "Сундук уже нельзя создать: вещи ранее были выброшены или обработаны.";
        }
        if (ACTION_RETURN.equals(action) && (record.returnPurchased || record.rescueCompleted || record.rescuePending || rescues.containsKey(record.playerId)))
            return record.rescueCompleted ? "Возврат для этой смерти уже выполнен." : "Возврат уже оплачен и выполняется. Повторного списания не будет.";
        if (ACTION_DROP.equals(action)) {
            if (record.chestPurchased && !record.chestCreated)
                return "Обычный дроп недоступен: сундук уже оплачен и создаётся. Повторного списания не будет.";
            if (record.itemsResolved)
                return record.chestCreated ? "Вещи уже находятся в сундуке." : "Вещи уже были выброшены.";
        }
        if (ACTION_BOTH.equals(action)) {
            boolean chestDone = record.chestCreated;
            boolean returnDone = record.returnPurchased || record.rescueCompleted || record.rescuePending || rescues.containsKey(record.playerId);
            if (record.chestPurchased && !record.chestCreated)
                return "Сундук уже оплачен и создаётся. Дождитесь результата; возврат после этого останется доступен отдельно.";
            if (chestDone && returnDone) return "Сундук и возврат для этой смерти уже выполнены.";
            if (!chestDone && record.itemsResolved) {
                return returnDone
                    ? "Сундук уже нельзя создать: вещи ранее были выброшены, а возврат уже выполнен."
                    : "Сундук уже нельзя создать: вещи ранее были выброшены. Можно отдельно выбрать возврат.";
            }
        }
        return null;
    }

    private void completeAction(DeathRecord record, String action) {
        synchronized (record) {
            record.actionsInFlight.remove(action);
            if (ACTION_BOTH.equals(action)) {
                record.actionsInFlight.remove(ACTION_CHEST);
                record.actionsInFlight.remove(ACTION_RETURN);
            }
            record.updatedAt = Instant.now();
            if (record.itemsResolved && record.coordinatesGranted && record.rescueCompleted) {
                record.offerClosed = true;
            }
            if (!record.offerClosed && Instant.now().isBefore(record.offerExpiresAt)) {
                record.stage = Stage.OFFER;
            }
            saveQuietly(record);
            plugin.debugDeath("death action completed deathId=" + record.deathId + " action=" + action
                + " offerClosed=" + record.offerClosed + " itemsResolved=" + record.itemsResolved
                + " coordinatesGranted=" + record.coordinatesGranted + " chestCreated=" + record.chestCreated
                + " rescueCompleted=" + record.rescueCompleted + " chargedAmount=" + record.chargedAmount
                + " remainingInFlight=" + record.actionsInFlight);
        }
    }

    private void failAction(Player player, DeathRecord record, String action, PurchaseResult result, String title) {
        synchronized (record) {
            record.actionsInFlight.remove(action);
            record.paymentErrorCode = result.reason;
            record.lastError = result.reason;
            if (!record.offerClosed && Instant.now().isBefore(record.offerExpiresAt)) record.stage = Stage.OFFER;
            saveQuietly(record);
        }
        plugin.debugDeath("death action failed deathId=" + record.deathId + " action=" + action
            + " title=" + title + " outcome=" + result.outcome + " reason=" + result.reason
            + " balance=" + result.balance + " cost=" + result.cost + " charged=" + result.charged
            + " itemsResolved=" + record.itemsResolved + " offerClosed=" + record.offerClosed);
        String message;
        if ("not-enough-rating".equals(result.reason)) {
            String balance = result.balance >= 0 ? String.valueOf(result.balance) : "неизвестно";
            String cost = result.cost > 0 ? String.valueOf(result.cost) : "неизвестно";
            message = title + " не выполнено: недостаточно баланса. Нужно " + cost + ", доступно " + balance + ". Можно выбрать другое действие.";
        } else {
            message = title + " не выполнено: " + purchaseReasonText(result.reason) + ". Можно выбрать другое действие.";
        }
        onPlayer(player, () -> {
            player.sendMessage(Component.text(message, NamedTextColor.RED));
            showOffer(player, record, true, "action-denied-" + action);
        }, 1L);
    }

    private String purchaseReasonText(String reason) {
        if (reason == null || reason.isBlank()) return "покупка отклонена";
        return switch (reason) {
            case "not-linked" -> "аккаунт Minecraft не привязан к TaskForge";
            case "cost-mismatch" -> "цена на сервере и сайте не совпадает";
            case "chest-place-not-reserved" -> "место для сундука ещё не подготовлено";
            case "purchase-already-compensated", "purchase-cancelled-after-fallback" -> "это списание уже отменено";
            case "death-not-found" -> "запись смерти не найдена";
            case "player-mismatch" -> "UUID игрока не совпал";
            case "linked-user-mismatch" -> "привязка указывает на другой аккаунт";
            case "linked-user-missing" -> "не удалось определить владельца привязки";
            case "request-id-collision" -> "обнаружен конфликт идентификатора операции; списание остановлено";
            case "purchase-denied" -> "сайт отклонил покупку без дополнительной причины";
            case "bad-request" -> "сайт отклонил параметры операции";
            default -> "сайт вернул причину «" + reason + "»";
        };
    }

    private void prepareBoth(Player player, DeathRecord record) {
        boolean needChest;
        boolean needReturn;
        synchronized (record) {
            needChest = !record.chestCreated && !record.chestPurchased && !record.itemsResolved;
            needReturn = !record.returnPurchased && !record.rescueCompleted && !record.rescuePending && !rescues.containsKey(record.playerId);
            if (!needChest && needReturn) {
                record.actionsInFlight.remove(ACTION_BOTH);
                record.actionsInFlight.add(ACTION_RETURN);
                record.action = ACTION_RETURN;
                saveQuietly(record);
            } else if (needChest && !needReturn) {
                record.actionsInFlight.remove(ACTION_BOTH);
                record.actionsInFlight.add(ACTION_CHEST);
                record.action = ACTION_CHEST;
                saveQuietly(record);
            }
        }
        if (needChest && needReturn) {
            prepareChestAndPurchase(player, record, true);
        } else if (needChest) {
            prepareChestAndPurchase(player, record, false);
        } else if (needReturn) {
            purchaseAndReturn(player, record);
        } else {
            completeAction(record, ACTION_BOTH);
            onPlayer(player, () -> player.sendMessage(Component.text(
                "Сундук и возврат уже выполнены; повторного списания не было.", NamedTextColor.YELLOW)), 1L);
        }
    }

    private void prepareChestAndPurchase(Player player, DeathRecord record, boolean withReturn) {
        String action = withReturn ? ACTION_BOTH : ACTION_CHEST;
        if (!beginChestSearch(record)) {
            completeAction(record, action);
            onPlayer(player, () -> player.sendMessage(Component.text(
                record.itemsResolved ? "Сундук уже нельзя создать: вещи ранее были обработаны." : "Поиск места для сундука уже выполняется.",
                NamedTextColor.YELLOW)), 1L);
            return;
        }
        if (deserializeItems(record.itemsBase64).size() > 54) {
            endChestSearch(record);
            completeAction(record, action);
            onPlayer(player, () -> {
                player.sendMessage(Component.text("Сундук не создан: предметов больше, чем помещается в двойной сундук. Баланс не списан.", NamedTextColor.RED));
                showOffer(player, record, true, "chest-too-many-items");
            }, 1L);
            return;
        }
        findAnyChestSpot(record, result -> {
            if (result.isEmpty()) {
                endChestSearch(record);
                record.lastError = "death-chest-location-not-found";
                completeAction(record, action);
                onPlayer(player, () -> {
                    player.sendMessage(Component.text(
                        "Сундук не создан: свободное место пока не найдено. Баланс не списан. Можно повторить попытку или выбрать другое действие.",
                        NamedTextColor.YELLOW));
                    showOffer(player, record, true, "chest-location-not-found");
                }, 1L);
                return;
            }
            ChestSpot spot = result.get();
            reserveChestSpot(record, spot);
            syncCreate(record).thenAccept(syncResult -> {
                if (syncResult == BackendResult.UNLINKED) {
                    endChestSearch(record);
                    completeAction(record, action);
                    plugin.markLinkStateUnlinked(record.playerId, "death-state-put");
                    handleBackendUnlinked(record, "backend-reservation-rejected-not-linked");
                    return;
                }
                if (syncResult == BackendResult.UNAVAILABLE) {
                    record.backendUnavailable = true;
                    saveQuietly(record);
                    placeChest(record, spot, placed -> {
                        endChestSearch(record);
                        completeAction(record, action);
                        if (placed) {
                            record.offerClosed = true;
                            notifyFreeChest(record, spot.first, "Сервис рейтинга недоступен — вещи сохранены бесплатно. Платные действия для этой смерти закрыты.");
                            finish(record, Stage.FREE_CHEST_CREATED);
                        } else {
                            scheduleGuaranteedChestRetry(record, "Сервис рейтинга недоступен — вещи сохранены системой до создания сундука.");
                        }
                    });
                    return;
                }

                int amount = withReturn ? chestCost + teleportCost : chestCost;
                purchase(record, action, amount).thenAccept(result2 -> {
                    if (result2.outcome == PurchaseOutcome.CONFIRMED) {
                        final int confirmedCharge = result2.charged > 0 ? result2.charged : amount;
                        synchronized (record) {
                            boolean wasChestPurchased = record.chestPurchased;
                            boolean wasReturnPurchased = record.returnPurchased;
                            if (withReturn) {
                                if (!wasChestPurchased && !wasReturnPurchased) record.chargedAmount += confirmedCharge;
                                record.chestPurchased = true;
                                record.returnPurchased = true;
                            } else {
                                if (!wasChestPurchased) record.chargedAmount += confirmedCharge;
                                record.chestPurchased = true;
                            }
                            record.paymentConfirmed = true;
                            record.compensated = false;
                            record.paymentErrorCode = "";
                            saveQuietly(record);
                        }
                        placeChest(record, spot, placed -> {
                            endChestSearch(record);
                            if (!placed) {
                                record.chestSpotReserved = false;
                                record.compensationAction = action;
                                record.compensationAmount = confirmedCharge;
                                saveQuietly(record);
                                requestCompensation(record, action, confirmedCharge);
                                completeAction(record, action);
                                createCompensatedChest(record,
                                    withReturn
                                        ? "Сундук не удалось создать. Списание " + confirmedCharge + " за сундук и возврат отправлено на возврат; возврат не запускался. Вещи будут сохранены бесплатно."
                                        : "Сундук не удалось создать. Списание " + confirmedCharge + " отправлено на возврат; вещи будут сохранены бесплатно.");
                            } else {
                                if (withReturn) {
                                    notifyChest(record, spot.first, false,
                                        "Сундук создан. Списано " + confirmedCharge + " за сундук и возврат. Возврат запускается.");
                                    record.rescuePending = true;
                                    saveQuietly(record);
                                    startRescueWhenOnline(record);
                                } else {
                                    notifyChest(record, spot.first, false,
                                        "Сундук создан. Списано " + confirmedCharge + ". Координаты и возврат всё ещё можно выбрать отдельно.");
                                    completeAction(record, action);
                                    finish(record, Stage.CHEST_CREATED);
                                    onPlayer(player, () -> showOffer(player, record, true, "chest-completed"), 2L);
                                }
                            }
                        });
                    } else if (result2.outcome == PurchaseOutcome.UNLINKED) {
                        endChestSearch(record);
                        completeAction(record, action);
                        plugin.markLinkStateUnlinked(record.playerId, "purchase");
                        handleBackendUnlinked(record, "backend-purchase-rejected-not-linked");
                    } else if (result2.outcome == PurchaseOutcome.UNAVAILABLE) {
                        record.backendUnavailable = true;
                        record.compensationAction = action;
                        record.compensationAmount = amount;
                        record.compensationPending = true;
                        saveQuietly(record);
                        placeChest(record, spot, placed -> {
                            endChestSearch(record);
                            completeAction(record, action);
                            if (placed) {
                                record.offerClosed = true;
                                notifyFreeChest(record, spot.first, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
                                finish(record, Stage.FREE_CHEST_CREATED);
                            } else {
                                scheduleGuaranteedChestRetry(record, "Сервис рейтинга недоступен — вещи сохранены системой до создания сундука.");
                            }
                            requestCompensation(record, action, amount);
                        });
                    } else {
                        endChestSearch(record);
                        record.chestSpotReserved = false;
                        failAction(player, record, action, result2, withReturn ? "Сундук и возврат" : "Сундук");
                    }
                });
            });
        });
    }

    private void purchaseCoordinates(Player player, DeathRecord record) {
        syncCreate(record).thenAccept(syncResult -> {
            if (syncResult == BackendResult.UNLINKED) {
                completeAction(record, ACTION_COORDINATES);
                plugin.markLinkStateUnlinked(record.playerId, "death-state-put");
                handleBackendUnlinked(record, "backend-coordinate-create-rejected-not-linked");
                return;
            }
            if (syncResult == BackendResult.UNAVAILABLE) {
                completeAction(record, ACTION_COORDINATES);
                record.backendUnavailable = true;
                saveQuietly(record);
                createFreeChest(record, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
                return;
            }
            purchase(record, ACTION_COORDINATES, coordinatesCost).thenAccept(result -> {
                if (result.outcome == PurchaseOutcome.CONFIRMED) {
                    synchronized (record) {
                        if (!record.coordinatesGranted) record.chargedAmount += result.charged;
                        record.paymentConfirmed = true;
                        record.compensated = false;
                        record.coordinatesGranted = true;
                        record.paymentErrorCode = "";
                        saveQuietly(record);
                    }
                    notifyDeathCoordinates(record);
                    completeAction(record, ACTION_COORDINATES);
                    onPlayer(player, () -> showOffer(player, record, true, "coordinates-completed"), 2L);
                } else if (result.outcome == PurchaseOutcome.UNLINKED) {
                    completeAction(record, ACTION_COORDINATES);
                    plugin.markLinkStateUnlinked(record.playerId, "purchase");
                    handleBackendUnlinked(record, "backend-coordinate-purchase-rejected-not-linked");
                } else if (result.outcome == PurchaseOutcome.UNAVAILABLE) {
                    completeAction(record, ACTION_COORDINATES);
                    record.backendUnavailable = true;
                    record.compensationAction = ACTION_COORDINATES;
                    record.compensationAmount = coordinatesCost;
                    record.compensationPending = true;
                    saveQuietly(record);
                    createFreeChest(record, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
                    requestCompensation(record, ACTION_COORDINATES, coordinatesCost);
                } else {
                    failAction(player, record, ACTION_COORDINATES, result, "Получение координат");
                }
            });
        });
    }

    private void purchaseAndReturn(Player player, DeathRecord record) {
        syncCreate(record).thenAccept(syncResult -> {
            if (syncResult == BackendResult.UNLINKED) {
                completeAction(record, ACTION_RETURN);
                plugin.markLinkStateUnlinked(record.playerId, "death-state-put");
                handleBackendUnlinked(record, "backend-return-create-rejected-not-linked");
                return;
            }
            if (syncResult == BackendResult.UNAVAILABLE) {
                completeAction(record, ACTION_RETURN);
                record.backendUnavailable = true;
                saveQuietly(record);
                createFreeChest(record, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
                return;
            }
            purchase(record, ACTION_RETURN, teleportCost).thenAccept(result -> {
                if (result.outcome == PurchaseOutcome.CONFIRMED) {
                    synchronized (record) {
                        if (!record.returnPurchased) record.chargedAmount += result.charged;
                        record.paymentConfirmed = true;
                        record.compensated = false;
                        record.returnPurchased = true;
                        record.rescuePending = true;
                        record.paymentErrorCode = "";
                        saveQuietly(record);
                    }
                    Runnable beginRescue = () -> startRescueWhenOnline(record);
                    if (!record.itemsResolved) {
                        releaseDrops(record, null, beginRescue);
                    } else {
                        beginRescue.run();
                    }
                } else if (result.outcome == PurchaseOutcome.UNLINKED) {
                    completeAction(record, ACTION_RETURN);
                    plugin.markLinkStateUnlinked(record.playerId, "purchase");
                    handleBackendUnlinked(record, "backend-return-purchase-rejected-not-linked");
                } else if (result.outcome == PurchaseOutcome.UNAVAILABLE) {
                    completeAction(record, ACTION_RETURN);
                    record.backendUnavailable = true;
                    record.compensationAction = ACTION_RETURN;
                    record.compensationAmount = teleportCost;
                    record.compensationPending = true;
                    saveQuietly(record);
                    createFreeChest(record, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
                    requestCompensation(record, ACTION_RETURN, teleportCost);
                } else {
                    failAction(player, record, ACTION_RETURN, result, "Возврат");
                }
            });
        });
    }

    void onLinkStateChanged(UUID playerId, TaskForgeLinkPlugin.LinkState state, String source) {
        if (playerId == null || state == null) return;
        plugin.debugDeath("link-state callback player=" + playerId + " state=" + state + " source=" + source
            + " records=" + deaths.values().stream().filter(r -> r.playerId.equals(playerId)).count());
        if (state == TaskForgeLinkPlugin.LinkState.LINKED) {
            Player player = plugin.findOnlinePlayer(playerId);
            if (player != null && player.isOnline() && !player.isDead()) {
                onPlayer(player, () -> {
                    showPendingOffers(player);
                    notifyUnseenChests(player);
                    resumeUnresolved(player);
                    resumePaidRescues(player);
                }, 1L);
                pullPending(player).exceptionally(error -> {
                    plugin.debugDeath("pending pull after link confirmation failed player=" + playerId + " error=" + error);
                    return null;
                });
            }
            return;
        }
        if (state != TaskForgeLinkPlugin.LinkState.UNLINKED) return;

        // Repair records captured by older plugin versions or by the tiny race between cache refresh
        // and an unlink operation. No purchase means the safest result is an immediate ordinary drop.
        for (DeathRecord record : new ArrayList<>(deaths.values())) {
            if (!record.playerId.equals(playerId)) continue;
            boolean release;
            synchronized (record) {
                release = !record.itemsResolved
                    && !record.paymentConfirmed
                    && record.chargedAmount <= 0
                    && (record.stage == Stage.WAITING_RESPAWN || record.stage == Stage.OFFER);
                if (release) {
                    record.stage = Stage.RESOLVING;
                    record.action = "unlinked-vanilla-release";
                    record.lastError = "link-state-became-unlinked:" + source;
                    saveQuietly(record);
                }
            }
            if (release) {
                plugin.debugDeath("releasing previously captured drops because player is unlinked deathId="
                    + record.deathId + " player=" + playerId + " source=" + source);
                releaseDrops(record, null, () -> queueVanillaDeathNotice(record.playerId, record.playerName,
                    TaskForgeLinkPlugin.LinkState.UNLINKED, countSerializedItems(record.itemsBase64),
                    record.worldKey, record.x, record.y, record.z, "link-state-callback"));
            }
        }
    }

    private void handleBackendUnlinked(DeathRecord record, String source) {
        plugin.debugDeath("backend rejected captured death as unlinked deathId=" + record.deathId
            + " player=" + record.playerId + " source=" + source + " itemsResolved=" + record.itemsResolved);
        synchronized (record) {
            if (record.itemsResolved || record.paymentConfirmed || record.chargedAmount > 0) return;
            record.stage = Stage.RESOLVING;
            record.action = "unlinked-vanilla-release";
            record.lastError = source;
            saveQuietly(record);
        }
        releaseDrops(record, null, () -> queueVanillaDeathNotice(record.playerId, record.playerName,
            TaskForgeLinkPlugin.LinkState.UNLINKED, countSerializedItems(record.itemsBase64),
            record.worldKey, record.x, record.y, record.z, source));
    }

    private String summarizeItems(List<ItemStack> items) {
        if (items == null || items.isEmpty()) return "[]";
        Map<String, Integer> totals = new LinkedHashMap<>();
        for (ItemStack item : items) {
            if (item == null || item.getType().isAir() || item.getAmount() <= 0) continue;
            totals.merge(item.getType().getKey().toString(), item.getAmount(), Integer::sum);
        }
        String text = totals.toString();
        return text.length() > 800 ? text.substring(0, 800) + "...<truncated>" : text;
    }

    private int countSerializedItems(String payload) {
        try { return deserializeItems(payload).size(); }
        catch (RuntimeException error) {
            plugin.getLogger().log(Level.WARNING, "Cannot count serialized death items for diagnostics", error);
            return -1;
        }
    }

    private void queueVanillaDeathNotice(Player player, TaskForgeLinkPlugin.LinkState state, int dropStacks,
                                         Location deathLocation, String source) {
        World world = deathLocation == null ? null : deathLocation.getWorld();
        queueVanillaDeathNotice(player.getUniqueId(), player.getName(), state, dropStacks,
            world == null ? "unknown" : world.getKey().toString(),
            deathLocation == null ? 0.0 : deathLocation.getX(),
            deathLocation == null ? 0.0 : deathLocation.getY(),
            deathLocation == null ? 0.0 : deathLocation.getZ(), source);
    }

    private void queueVanillaDeathNotice(UUID playerId, String playerName, TaskForgeLinkPlugin.LinkState state,
                                         int dropStacks, String worldKey, double x, double y, double z, String source) {
        PendingVanillaDeathNotice notice = new PendingVanillaDeathNotice(playerId, playerName, state,
            dropStacks, worldKey, x, y, z, Instant.now(), source);
        PendingVanillaDeathNotice previous = vanillaDeathNotices.put(playerId, notice);
        plugin.debugDeath("vanilla death notice queued player=" + playerName + "/" + playerId
            + " state=" + state + " dropStacks=" + dropStacks + " world=" + worldKey
            + " xyz=" + x + "," + y + "," + z + " source=" + source
            + " replacedPrevious=" + (previous != null));
        Player player = plugin.findOnlinePlayer(playerId);
        if (player != null) deliverVanillaDeathNotice(player, "queue");
    }

    private void deliverVanillaDeathNotice(Player player, String trigger) {
        if (player == null) return;
        PendingVanillaDeathNotice notice = vanillaDeathNotices.get(player.getUniqueId());
        if (notice == null) return;
        synchronized (notice) {
            if (notice.dispatchPending) {
                plugin.debugDeath("vanilla death notice delivery already pending player=" + player.getUniqueId()
                    + " trigger=" + trigger);
                return;
            }
            notice.dispatchPending = true;
        }
        plugin.debugDeath("vanilla death notice delivery queued player=" + player.getName() + "/" + player.getUniqueId()
            + " trigger=" + trigger + " dead=" + player.isDead() + " online=" + player.isOnline()
            + " state=" + notice.state + " source=" + notice.source);
        onPlayer(player, () -> {
            if (!player.isOnline() || player.isDead()) {
                synchronized (notice) { notice.dispatchPending = false; }
                plugin.debugDeath("vanilla death notice deferred player=" + player.getUniqueId()
                    + " trigger=" + trigger + " dead=" + player.isDead() + " online=" + player.isOnline());
                return;
            }
            if (notice.state == TaskForgeLinkPlugin.LinkState.UNLINKED) {
                player.sendMessage("§eTaskForge не привязан.");
                player.sendMessage("§7Привяжите Minecraft в профиле §ftaskforge.by§7, чтобы восстанавливать вещи, получать координаты смерти и возвращаться назад.");
            } else {
                player.sendMessage("§eTaskForge: §7не удалось подтвердить привязку сайта во время смерти.");
                player.sendMessage("§7Чтобы не потерять предметы из-за сбоя проверки, смерть обработана полностью обычным способом.");
            }
            player.sendMessage("§7Ваши вещи и опыт обработаны Minecraft как обычно; TaskForge их не перехватывал.");
            boolean removed = vanillaDeathNotices.remove(player.getUniqueId(), notice);
            plugin.debugDeath("vanilla death notice delivered player=" + player.getName() + "/" + player.getUniqueId()
                + " trigger=" + trigger + " state=" + notice.state + " dropStacks=" + notice.dropStacks
                + " world=" + notice.worldKey + " xyz=" + notice.x + "," + notice.y + "," + notice.z
                + " removed=" + removed);
        }, () -> {
            synchronized (notice) { notice.dispatchPending = false; }
            plugin.debugDeath("vanilla death notice scheduler retired player=" + player.getUniqueId()
                + " trigger=" + trigger);
        }, 2L);
    }

    private void heartbeat() {
        if (stopped.get()) return;
        Instant now = Instant.now();
        int notificationOwed = 0;
        int unresolved = 0;
        int compensationPending = 0;
        int vanillaNoticesPending = vanillaDeathNotices.size();
        for (PendingVanillaDeathNotice notice : new ArrayList<>(vanillaDeathNotices.values())) {
            Player noticePlayer = plugin.findOnlinePlayer(notice.playerId);
            if (noticePlayer != null && noticePlayer.isOnline() && !noticePlayer.isDead()) {
                deliverVanillaDeathNotice(noticePlayer, "heartbeat");
            }
        }
        for (DeathRecord record : new ArrayList<>(deaths.values())) {
            Player notificationPlayer = plugin.findOnlinePlayer(record.playerId);
            if (record.coordinatesGranted && !record.coordinatesNotified) {
                notificationOwed++;
                if (notificationPlayer != null && notificationPlayer.isOnline() && !notificationPlayer.isDead()) notifyDeathCoordinates(record);
            }
            if (record.chestCreated && !record.chestNotified) {
                notificationOwed++;
                if (notificationPlayer != null && notificationPlayer.isOnline() && !notificationPlayer.isDead()) {
                    Optional<World> notificationWorld = resolveWorld(record);
                    if (notificationWorld.isPresent()) {
                        Location chestAt = new Location(notificationWorld.get(), record.chestX, record.chestY, record.chestZ);
                        boolean free = record.backendUnavailable || record.compensated;
                        notifyChest(record, chestAt, free, free
                            ? "Сервис рейтинга был недоступен — вещи бесплатно сохранены в сундуке."
                            : "Сундук смерти создан.");
                    }
                }
            }
            if (!record.offerClosed || record.compensationPending || record.rescuePending || (record.backendUnavailable && !record.itemsResolved)) unresolved++;
            if (record.compensationPending) compensationPending++;
            if (!record.offerClosed && now.isAfter(record.offerExpiresAt)) expire(record);
            if (record.compensationPending && record.compensationAmount > 0 && claimCompensationRetry(record)) {
                requestCompensation(record,
                    record.compensationAction == null || record.compensationAction.isBlank() ? record.action : record.compensationAction,
                    record.compensationAmount);
            }
            if (record.backendUnavailable && !record.itemsResolved && claimRetry(record)) {
                createFreeChest(record, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
                continue;
            }
            if (record.compensated && !record.itemsResolved
                && (ACTION_CHEST.equals(record.action) || ACTION_BOTH.equals(record.action)) && claimRetry(record)) {
                createCompensatedChest(record, "Платёж возвращён — вещи сохранены бесплатно в сундуке.");
                continue;
            }
            if (notificationPlayer != null && notificationPlayer.isOnline()
                && (record.compensationPending || record.rescuePending || record.stage == Stage.RESOLVING || record.stage == Stage.WORLD_UNAVAILABLE)) {
                resumeUnresolved(notificationPlayer);
            }
        }
        for (RescueSession session : new ArrayList<>(rescues.values())) tickRescue(session, now);
        if (plugin.debugHeartbeat() && (!deaths.isEmpty() || plugin.onlinePlayersSnapshot().size() > 0)) {
            plugin.debug("heartbeat", "records=" + deaths.size() + " unresolved=" + unresolved
                + " notificationOwed=" + notificationOwed + " vanillaNoticesPending=" + vanillaNoticesPending
                + " compensationPending=" + compensationPending + " activeRescues=" + rescues.size()
                + " onlinePlayers=" + plugin.onlinePlayersSnapshot().size() + " linkCache={" + plugin.linkStateCounts() + "}");
        }
    }

    private void expire(DeathRecord record) {
        synchronized (record) {
            if (record.offerClosed) return;
            record.offerClosed = true;
            record.actionsInFlight.clear();
            record.action = "expired";
            record.updatedAt = Instant.now();
            saveQuietly(record);
        }
        if (record.itemsResolved) {
            plugin.debugDeath("offer expired after items already resolved deathId=" + record.deathId
                + " coordinatesGranted=" + record.coordinatesGranted + " rescueCompleted=" + record.rescueCompleted);
            return;
        }
        health(record.playerId).thenAccept(healthy -> {
            if (healthy) releaseDrops(record, "Время выбора истекло. Вещи выпали в месте смерти.");
            else createFreeChest(record, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
        });
    }

    private Set<UUID> deathIdsFor(UUID playerId) {
        Set<UUID> ids = new HashSet<>();
        for (DeathRecord record : deaths.values()) {
            if (record.playerId.equals(playerId)) ids.add(record.deathId);
        }
        return ids;
    }

    private void showPendingOffers(Player player) {
        showPendingOffersExcluding(player, Set.of(), false, "generic");
    }

    private void showPendingOffersExcluding(Player player, Set<UUID> excludedDeathIds) {
        showPendingOffersExcluding(player, excludedDeathIds, false, "backend-pull");
    }

    private void showPendingOffersExcluding(Player player, Set<UUID> excludedDeathIds, boolean force, String trigger) {
        TaskForgeLinkPlugin.LinkState linkState = plugin.linkState(player.getUniqueId());
        if (linkState != TaskForgeLinkPlugin.LinkState.LINKED) {
            plugin.debugDeath("pending offers withheld player=" + player.getName() + "/" + player.getUniqueId()
                + " reason=link-not-confirmed " + plugin.linkStateDebug(player.getUniqueId()));
            return;
        }
        deaths.values().stream()
            .filter(r -> r.playerId.equals(player.getUniqueId()))
            .filter(r -> !excludedDeathIds.contains(r.deathId))
            .filter(this::isOfferOpen)
            .sorted(Comparator.comparing(r -> r.createdAt))
            .forEach(r -> showOffer(player, r, force, trigger));
    }

    private boolean isOfferOpen(DeathRecord record) {
        return !record.offerClosed && record.offerExpiresAt != null && Instant.now().isBefore(record.offerExpiresAt)
            && !(record.backendUnavailable && record.chestCreated);
    }

    private void showOffer(Player player, DeathRecord r, boolean force, String trigger) {
        Instant now = Instant.now();
        synchronized (r) {
            if (!isOfferOpen(r)) return;
            if (!force && r.lastOfferShownAt != null && Duration.between(r.lastOfferShownAt, now).compareTo(OFFER_DISPLAY_COOLDOWN) < 0) {
                plugin.debugDeath("offer display suppressed deathId=" + r.deathId + " player=" + player.getUniqueId()
                    + " trigger=" + trigger + " lastShownAt=" + r.lastOfferShownAt);
                return;
            }
            r.lastOfferShownAt = now;
            r.stage = Stage.OFFER;
            saveQuietly(r);
        }
        long left = Math.max(0, Duration.between(Instant.now(), r.offerExpiresAt).toSeconds());
        Component line = Component.text("Смерть: ", NamedTextColor.GRAY);
        int buttons = 0;
        if (!r.coordinatesGranted) {
            line = line.append(button("[Координаты — " + coordinatesCost + "]", r, ACTION_COORDINATES, NamedTextColor.LIGHT_PURPLE)).append(Component.space());
            buttons++;
        }
        if (!r.chestCreated && !r.chestPurchased && !r.itemsResolved) {
            line = line.append(button("[Сундук — " + chestCost + "]", r, ACTION_CHEST, NamedTextColor.GOLD)).append(Component.space());
            buttons++;
        }
        if (!r.returnPurchased && !r.rescueCompleted && !r.rescuePending && !rescues.containsKey(r.playerId)) {
            line = line.append(button("[Вернуться — " + teleportCost + "]", r, ACTION_RETURN, NamedTextColor.AQUA)).append(Component.space());
            buttons++;
        }
        boolean chestAvailable = !r.chestCreated && !r.chestPurchased && !r.itemsResolved;
        boolean returnAvailable = !r.returnPurchased && !r.rescueCompleted && !r.rescuePending && !rescues.containsKey(r.playerId);
        if (chestAvailable && returnAvailable) {
            line = line.append(button("[Сундук + возврат — " + (chestCost + teleportCost) + "]", r, ACTION_BOTH, NamedTextColor.GREEN)).append(Component.space());
            buttons++;
        }
        if (!r.itemsResolved) {
            line = line.append(button("[Обычный дроп]", r, ACTION_DROP, NamedTextColor.WHITE));
            buttons++;
        }
        if (buttons == 0) {
            plugin.debugDeath("offer display skipped because no actions remain deathId=" + r.deathId
                + " player=" + player.getUniqueId() + " trigger=" + trigger
                + " returnPurchased=" + r.returnPurchased + " rescuePending=" + r.rescuePending
                + " compensationPending=" + r.compensationPending);
            return;
        }
        player.sendMessage(line);
        player.sendMessage(Component.text("Можно выполнить несколько действий. Осталось " + left + " сек.", NamedTextColor.GRAY));
        plugin.debugDeath("offer delivered deathId=" + r.deathId + " player=" + player.getUniqueId()
            + " secondsLeft=" + left + " trigger=" + trigger + " force=" + force
            + " coordinatesGranted=" + r.coordinatesGranted + " chestCreated=" + r.chestCreated
            + " itemsResolved=" + r.itemsResolved + " rescueCompleted=" + r.rescueCompleted);
    }

    private Component button(String text, DeathRecord r, String action, NamedTextColor color) {
        return Component.text(text, color).clickEvent(ClickEvent.runCommand("/tfdeath " + r.deathId + " " + action));
    }

    private void resumePaidRescues(Player player) {
        deaths.values().stream()
            .filter(r -> r.playerId.equals(player.getUniqueId()) && r.paymentConfirmed && r.rescuePending && !r.rescueCompleted)
            .findFirst().ifPresent(this::startRescueWhenOnline);
    }

    private CompletableFuture<Void> pullPending(Player player) {
        if (plugin.linkState(player.getUniqueId()) != TaskForgeLinkPlugin.LinkState.LINKED) {
            plugin.debugDeath("pending backend pull skipped player=" + player.getUniqueId()
                + " reason=link-not-confirmed " + plugin.linkStateDebug(player.getUniqueId()));
            return CompletableFuture.completedFuture(null);
        }
        if (apiBaseUrl.isBlank() || serverToken.isBlank()) return CompletableFuture.completedFuture(null);
        return request("GET", API_PATH + "/player/" + player.getUniqueId() + "/pending", null)
            .thenAccept(response -> {
                if (response == null || response.statusCode() < 200 || response.statusCode() >= 300) return;
                try {
                    JsonElement parsed = JsonParser.parseString(response.body());
                    if (!parsed.isJsonArray()) return;
                    JsonArray rows = parsed.getAsJsonArray();
                    for (JsonElement element : rows) {
                        if (!element.isJsonObject()) continue;
                        DeathRecord remote = fromBackend(element.getAsJsonObject());
                        if (remote == null || !remote.playerId.equals(player.getUniqueId())) continue;
                        deaths.compute(remote.deathId, (id, local) -> {
                            if (local == null) {
                                saveQuietly(remote);
                                return remote;
                            }
                            synchronized (local) {
                                if (remote.revision > local.revision) mergeRemote(local, remote);
                                saveQuietly(local);
                                return local;
                            }
                        });
                    }
                } catch (RuntimeException ex) {
                    plugin.getLogger().log(Level.WARNING, "Cannot parse pending death recoveries", ex);
                }
            })
            .exceptionally(ex -> null);
    }

    private void resumeUnresolved(Player player) {
        for (DeathRecord record : new ArrayList<>(deaths.values())) {
            if (!record.playerId.equals(player.getUniqueId())) continue;

            if (record.compensationPending && record.compensationAmount > 0 && claimCompensationRetry(record)) {
                requestCompensation(record,
                    record.compensationAction == null || record.compensationAction.isBlank() ? record.action : record.compensationAction,
                    record.compensationAmount);
                continue;
            }
            if (record.backendUnavailable && !record.itemsResolved && claimRetry(record)) {
                createFreeChest(record, "Сервис рейтинга недоступен — вещи сохранены бесплатно.");
                continue;
            }
            if (record.coordinatesGranted && !record.coordinatesNotified) notifyDeathCoordinates(record);
            if (record.chestCreated && !record.chestNotified) {
                Optional<World> world = resolveWorld(record);
                world.ifPresent(value -> notifyChest(record,
                    new Location(value, record.chestX, record.chestY, record.chestZ),
                    record.backendUnavailable || record.compensated,
                    record.backendUnavailable || record.compensated
                        ? "Вещи бесплатно сохранены в сундуке."
                        : "Сундук смерти создан."));
            }
            if (record.rescuePending && record.paymentConfirmed && claimRetry(record)) {
                if (!record.itemsResolved && ACTION_RETURN.equals(record.action)) {
                    releaseDrops(record, null, () -> startRescueWhenOnline(record));
                } else {
                    startRescueWhenOnline(record);
                }
                continue;
            }
            if (record.stage == Stage.WORLD_UNAVAILABLE && resolveWorld(record).isPresent()) {
                record.stage = record.offerClosed ? Stage.RESOLVING : Stage.OFFER;
                saveQuietly(record);
            }
            if (record.stage != Stage.RESOLVING || !claimRetry(record)) continue;

            plugin.debugDeath("resume unresolved deathId=" + record.deathId + " action=" + record.action
                + " paymentConfirmed=" + record.paymentConfirmed + " chestPurchased=" + record.chestPurchased
                + " returnPurchased=" + record.returnPurchased + " coordinatesGranted=" + record.coordinatesGranted
                + " itemsResolved=" + record.itemsResolved + " offerClosed=" + record.offerClosed);

            if (ACTION_COORDINATES.equals(record.action)) {
                if (record.coordinatesGranted) {
                    notifyDeathCoordinates(record);
                    completeAction(record, ACTION_COORDINATES);
                } else {
                    record.actionsInFlight.add(ACTION_COORDINATES);
                    purchaseCoordinates(player, record);
                }
                continue;
            }
            if ((ACTION_CHEST.equals(record.action) || ACTION_BOTH.equals(record.action)) && !record.itemsResolved) {
                record.actionsInFlight.add(record.action);
                prepareChestAndPurchase(player, record, ACTION_BOTH.equals(record.action));
                continue;
            }
            if (ACTION_RETURN.equals(record.action) && !record.rescueCompleted) {
                if (record.returnPurchased) {
                    record.rescuePending = true;
                    saveQuietly(record);
                    if (!record.itemsResolved) releaseDrops(record, null, () -> startRescueWhenOnline(record));
                    else startRescueWhenOnline(record);
                } else {
                    record.actionsInFlight.add(ACTION_RETURN);
                    purchaseAndReturn(player, record);
                }
                continue;
            }
            if ((ACTION_DROP.equals(record.action) || "expired".equals(record.action)) && !record.itemsResolved) {
                releaseDrops(record, "Вещи выпали в месте смерти.");
                continue;
            }
            if (!record.offerClosed) {
                record.stage = Stage.OFFER;
                record.actionsInFlight.clear();
                saveQuietly(record);
            }
        }
    }

    private boolean claimRetry(DeathRecord record) {
        synchronized (record) {
            Instant now = Instant.now();
            if (record.retryAfter != null && now.isBefore(record.retryAfter)) return false;
            record.retryAfter = now.plusSeconds(15);
            return true;
        }
    }

    private boolean claimCompensationRetry(DeathRecord record) {
        synchronized (record) {
            Instant now = Instant.now();
            if (record.compensationRetryAfter != null && now.isBefore(record.compensationRetryAfter)) return false;
            record.compensationRetryAfter = now.plusSeconds(15);
            return true;
        }
    }

    private void deferRetry(DeathRecord record) {
        synchronized (record) {
            record.retryAfter = Instant.now().plusSeconds(15);
        }
    }

    private DeathRecord fromBackend(JsonObject json) {
        try {
            DeathRecord r = new DeathRecord();
            r.deathId = UUID.fromString(text(json, "deathId", ""));
            r.playerId = UUID.fromString(text(json, "playerUuid", ""));
            r.playerName = text(json, "playerName", "");
            r.worldUuid = UUID.fromString(text(json, "worldUuid", ""));
            r.worldKey = text(json, "worldKey", "");
            r.worldName = text(json, "worldName", "");
            r.x = number(json, "x", 0); r.y = number(json, "y", 0); r.z = number(json, "z", 0);
            r.yaw = (float) number(json, "yaw", 0); r.pitch = (float) number(json, "pitch", 0);
            r.itemsBase64 = text(json, "itemsPayload", "");
            r.createdAt = instant(json, "createdAtUtc", Instant.now());
            r.offerExpiresAt = instant(json, "offerExpiresAtUtc", r.createdAt.plusSeconds(offerSeconds));
            r.rescueEndsAt = instant(json, "rescueEndsAtUtc", null);
            try { r.stage = Stage.valueOf(text(json, "stage", "OFFER").toUpperCase(Locale.ROOT)); }
            catch (IllegalArgumentException ex) { r.stage = Stage.OFFER; }
            r.action = text(json, "action", "");
            String purchaseId = text(json, "purchaseRequestId", "");
            try { r.requestId = purchaseId.isBlank() ? UUID.randomUUID() : UUID.fromString(purchaseId); }
            catch (IllegalArgumentException ignored) { r.requestId = UUID.randomUUID(); }
            r.chargedAmount = integer(json, "chargedAmount", 0);
            String paymentStatus = text(json, "paymentStatus", "none");
            r.paymentConfirmed = "confirmed".equalsIgnoreCase(paymentStatus) || "compensated".equalsIgnoreCase(paymentStatus);
            if (r.paymentConfirmed && ACTION_COORDINATES.equals(r.action)) r.coordinatesGranted = true;
            if (r.paymentConfirmed && (ACTION_CHEST.equals(r.action) || ACTION_BOTH.equals(r.action))) r.chestPurchased = true;
            if (r.paymentConfirmed && (ACTION_RETURN.equals(r.action) || ACTION_BOTH.equals(r.action))) r.returnPurchased = true;
            r.paymentErrorCode = text(json, "paymentErrorCode", "");
            r.dropsReleased = bool(json, "dropsReleased");
            r.chestSpotReserved = bool(json, "chestSpotReserved");
            r.chestCreated = bool(json, "chestCreated");
            r.itemsResolved = bool(json, "itemsResolved");
            r.rescuePending = bool(json, "rescuePending");
            r.rescueCompleted = bool(json, "rescueCompleted");
            r.previousGameMode = text(json, "previousGameMode", "");
            r.backendUnavailable = bool(json, "backendUnavailable");
            r.compensationPending = bool(json, "compensationPending");
            r.compensated = bool(json, "compensated");
            r.chestX = integer(json, "chestX", 0); r.chestY = integer(json, "chestY", 0); r.chestZ = integer(json, "chestZ", 0);
            r.chestSecondX = integer(json, "chestSecondX", 0); r.chestSecondY = integer(json, "chestSecondY", 0); r.chestSecondZ = integer(json, "chestSecondZ", 0);
            r.finalX = number(json, "finalX", 0); r.finalY = number(json, "finalY", 0); r.finalZ = number(json, "finalZ", 0);
            r.lastError = text(json, "lastError", "");
            r.revision = longNumber(json, "revision", 0L);
            r.updatedAt = instant(json, "updatedAtUtc", r.createdAt);
            r.offerClosed = r.offerExpiresAt != null && Instant.now().isAfter(r.offerExpiresAt);
            return r;
        } catch (RuntimeException ex) {
            plugin.getLogger().log(Level.WARNING, "Invalid backend death recovery row", ex);
            return null;
        }
    }

    private void mergeRemote(DeathRecord local, DeathRecord remote) {
        local.playerName = remote.playerName;
        local.worldUuid = remote.worldUuid;
        local.worldKey = remote.worldKey;
        local.worldName = remote.worldName;
        local.x = remote.x; local.y = remote.y; local.z = remote.z;
        local.yaw = remote.yaw; local.pitch = remote.pitch;
        if ((local.itemsBase64 == null || local.itemsBase64.isBlank()) && remote.itemsBase64 != null)
            local.itemsBase64 = remote.itemsBase64;
        local.createdAt = remote.createdAt;
        local.offerExpiresAt = remote.offerExpiresAt;
        local.rescueEndsAt = remote.rescueEndsAt;
        local.stage = remote.stage;
        local.action = remote.action;
        if (remote.requestId != null) local.requestId = remote.requestId;
        local.chargedAmount = Math.max(local.chargedAmount, remote.chargedAmount);
        local.paymentConfirmed |= remote.paymentConfirmed;
        local.coordinatesGranted |= remote.coordinatesGranted;
        local.chestPurchased |= remote.chestPurchased;
        local.returnPurchased |= remote.returnPurchased;
        local.paymentErrorCode = remote.paymentErrorCode;
        local.dropsReleased |= remote.dropsReleased;
        local.chestSpotReserved |= remote.chestSpotReserved;
        local.chestCreated |= remote.chestCreated;
        local.itemsResolved |= remote.itemsResolved;
        local.rescuePending = remote.rescuePending;
        local.rescueCompleted |= remote.rescueCompleted;
        if (local.previousGameMode == null || local.previousGameMode.isBlank()) local.previousGameMode = remote.previousGameMode;
        local.backendUnavailable |= remote.backendUnavailable;
        local.compensationPending = remote.compensationPending;
        local.compensated |= remote.compensated;
        if (remote.chestSpotReserved) {
            local.chestX = remote.chestX; local.chestY = remote.chestY; local.chestZ = remote.chestZ;
            local.chestSecondX = remote.chestSecondX; local.chestSecondY = remote.chestSecondY; local.chestSecondZ = remote.chestSecondZ;
        }
        local.finalX = remote.finalX; local.finalY = remote.finalY; local.finalZ = remote.finalZ;
        local.lastError = remote.lastError;
        local.revision = remote.revision;
        local.updatedAt = remote.updatedAt;
    }

    private String text(JsonObject json, String key, String fallback) {
        JsonElement value = json.get(key);
        return value == null || value.isJsonNull() ? fallback : value.getAsString();
    }

    private double number(JsonObject json, String key, double fallback) {
        JsonElement value = json.get(key);
        try { return value == null || value.isJsonNull() ? fallback : value.getAsDouble(); }
        catch (RuntimeException ex) { return fallback; }
    }

    private int integer(JsonObject json, String key, int fallback) {
        JsonElement value = json.get(key);
        try { return value == null || value.isJsonNull() ? fallback : value.getAsInt(); }
        catch (RuntimeException ex) { return fallback; }
    }

    private long longNumber(JsonObject json, String key, long fallback) {
        JsonElement value = json.get(key);
        try { return value == null || value.isJsonNull() ? fallback : value.getAsLong(); }
        catch (RuntimeException ex) { return fallback; }
    }

    private boolean bool(JsonObject json, String key) {
        JsonElement value = json.get(key);
        try { return value != null && !value.isJsonNull() && value.getAsBoolean(); }
        catch (RuntimeException ex) { return false; }
    }

    private Instant instant(JsonObject json, String key, Instant fallback) {
        String value = text(json, key, "");
        try { return value.isBlank() ? fallback : Instant.parse(value); }
        catch (RuntimeException ex) { return fallback; }
    }

    private void startRescueWhenOnline(DeathRecord record) {
        if (rescues.containsKey(record.playerId)) return;
        Player player = plugin.findOnlinePlayer(record.playerId);
        if (player == null || !player.isOnline()) {
            record.rescuePending = true;
            record.stage = Stage.RESCUE_PENDING;
            saveQuietly(record);
            return;
        }
        Optional<Location> centerOpt = deathLocation(record);
        if (centerOpt.isEmpty()) {
            record.stage = Stage.WORLD_UNAVAILABLE;
            record.rescuePending = true;
            saveQuietly(record);
            onPlayer(player, () -> player.sendMessage(Component.text(
                "Мир смерти пока не загружен. Оплаченный возврат продолжится автоматически.", NamedTextColor.YELLOW)), 1L);
            return;
        }
        Location center = centerOpt.get();
        onPlayer(player, () -> {
            if (record.previousGameMode == null || record.previousGameMode.isBlank())
                record.previousGameMode = player.getGameMode().name();
            Location fallback = player.getLocation().clone();
            record.rescuePending = true;
            record.stage = Stage.RESCUE_ACTIVE;
            record.rescueEndsAt = Instant.now().plusSeconds(spectatorSeconds);
            saveQuietly(record);
            RescueSession session = new RescueSession(record, center, fallback, record.rescueEndsAt);
            rescues.put(player.getUniqueId(), session);
            session.internalTeleport = true;
            player.teleportAsync(center).whenComplete((ok, err) -> onPlayer(player, () -> {
                session.internalTeleport = false;
                if (err != null || !Boolean.TRUE.equals(ok)) {
                    rescues.remove(player.getUniqueId());
                    restoreGameMode(player, record);
                    record.stage = Stage.RESCUE_PENDING;
                    record.rescuePending = true;
                    saveQuietly(record);
                    player.sendMessage(Component.text("Возврат будет повторён, когда мир станет доступен.", NamedTextColor.YELLOW));
                    return;
                }
                player.setGameMode(GameMode.SPECTATOR);
                player.showTitle(Title.title(Component.text("Найдите точку появления", NamedTextColor.AQUA),
                    Component.text(Integer.toString(spectatorSeconds), NamedTextColor.WHITE)));
            }, 1L));
        }, 1L);
    }

    private void tickRescue(RescueSession session, Instant now) {
        Player player = plugin.findOnlinePlayer(session.record.playerId);
        if (player == null || !player.isOnline()) return;
        onPlayer(player, () -> {
            if (!player.getWorld().getUID().equals(session.record.worldUuid)) {
                teleportInternal(session, player, session.center);
                return;
            }
            Location current = player.getLocation();
            Vector delta = current.toVector().subtract(session.center.toVector());
            if (delta.lengthSquared() > (double) maxDistance * maxDistance) {
                Vector clamped = delta.normalize().multiply(maxDistance - 0.25);
                Location boundary = session.center.clone().add(clamped);
                boundary.setYaw(current.getYaw()); boundary.setPitch(current.getPitch());
                teleportInternal(session, player, boundary);
            }
            int left = (int) Math.ceil(Math.max(0, Duration.between(now, session.endsAt).toMillis()) / 1000.0);
            if (left > 0) {
                player.sendActionBar(Component.text("Найдите точку появления: " + left, NamedTextColor.AQUA));
                return;
            }

            rescues.remove(player.getUniqueId());
            findSafeLocation(session.record, player.getLocation(), found -> {
                if (found.isPresent()) {
                    completeRescue(session, player, found.get());
                    return;
                }
                // The player's chosen point may be inside lava/void. Try the death centre once more,
                // then always return to the safe location captured before spectator mode.
                findSafeLocation(session.record, session.center, centerFound -> {
                    if (centerFound.isPresent()) completeRescue(session, player, centerFound.get());
                    else completeRescueFallback(session, player,
                        "Безопасная точка рядом со смертью не найдена. Вы возвращены в исходную точку.");
                });
            });
        }, 1L);
    }

    private void completeRescue(RescueSession session, Player player, Location destination) {
        session.internalTeleport = true;
        player.teleportAsync(destination).whenComplete((ok, err) -> onPlayer(player, () -> {
            session.internalTeleport = false;
            if (err != null || !Boolean.TRUE.equals(ok)) {
                completeRescueFallback(session, player,
                    "Телепортация в выбранную точку не удалась. Вы возвращены в исходную точку.");
                return;
            }
            restoreGameMode(player, session.record);
            session.record.finalX = destination.getX();
            session.record.finalY = destination.getY();
            session.record.finalZ = destination.getZ();
            session.record.rescuePending = false;
            session.record.rescueCompleted = true;
            String completedAction = ACTION_BOTH.equals(session.record.action) ? ACTION_BOTH : ACTION_RETURN;
            completeAction(session.record, completedAction);
            finish(session.record, session.record.chestCreated ? Stage.CHEST_AND_RESCUE_COMPLETED : Stage.RESCUE_COMPLETED);
            player.sendMessage(Component.text(returnCompletionMessage(session.record), NamedTextColor.GREEN));
            showOffer(player, session.record, true, "return-completed");
        }, 1L));
    }

    private void completeRescueFallback(RescueSession session, Player player, String message) {
        session.internalTeleport = true;
        player.teleportAsync(session.fallback).whenComplete((ok, err) -> onPlayer(player, () -> {
            session.internalTeleport = false;
            restoreGameMode(player, session.record);
            Location finalLocation = Boolean.TRUE.equals(ok) && err == null ? session.fallback : player.getLocation();
            session.record.finalX = finalLocation.getX();
            session.record.finalY = finalLocation.getY();
            session.record.finalZ = finalLocation.getZ();
            session.record.rescuePending = false;
            session.record.rescueCompleted = true;
            session.record.lastError = Boolean.TRUE.equals(ok) && err == null ? "safe-location-not-found" : "fallback-teleport-failed";
            String completedAction = ACTION_BOTH.equals(session.record.action) ? ACTION_BOTH : ACTION_RETURN;
            completeAction(session.record, completedAction);
            finish(session.record, session.record.chestCreated ? Stage.CHEST_AND_RESCUE_COMPLETED : Stage.RESCUE_COMPLETED);
            player.sendMessage(Component.text(message, NamedTextColor.YELLOW));
            player.sendMessage(Component.text(returnCompletionMessage(session.record), NamedTextColor.GREEN));
            showOffer(player, session.record, true, "return-fallback-completed");
        }, 1L));
    }

    private String returnCompletionMessage(DeathRecord record) {
        if (ACTION_BOTH.equals(record.action)) {
            return "Возврат завершён. Сундук и возврат оплачены общей операцией; дополнительного списания не было.";
        }
        return "Возврат завершён. Списано " + teleportCost + ". Координаты смерти всё ещё можно получить отдельно до конца времени выбора.";
    }

    private void restoreInterruptedSpectator(Player player) {
        if (player.getGameMode() != GameMode.SPECTATOR || rescues.containsKey(player.getUniqueId())) return;
        deaths.values().stream()
            .filter(record -> record.playerId.equals(player.getUniqueId()))
            .filter(record -> record.rescuePending && !record.rescueCompleted)
            .filter(record -> record.previousGameMode != null && !record.previousGameMode.isBlank())
            .max(Comparator.comparing(record -> record.updatedAt))
            .ifPresent(record -> restoreGameMode(player, record));
    }

    private void restoreGameMode(Player player, DeathRecord record) {
        GameMode restore = GameMode.SURVIVAL;
        try {
            if (record.previousGameMode != null && !record.previousGameMode.isBlank())
                restore = GameMode.valueOf(record.previousGameMode);
        } catch (IllegalArgumentException ignored) { }
        if (player.getGameMode() == GameMode.SPECTATOR || player.getGameMode() != restore) {
            player.setGameMode(restore);
        }
    }

    private void teleportInternal(RescueSession session, Player player, Location location) {
        session.internalTeleport = true;
        player.teleportAsync(location).whenComplete((ok, err) -> session.internalTeleport = false);
    }

    private Optional<World> resolveWorld(DeathRecord record) {
        // The exact death world is resolved without any hub/first-world/Terra fallback.
        World world = record.worldUuid == null ? null : Bukkit.getWorld(record.worldUuid);
        if (world != null) return Optional.of(world);

        NamespacedKey key = NamespacedKey.fromString(record.worldKey == null ? "" : record.worldKey);
        if (key != null) {
            world = Bukkit.getWorld(key);
            if (world != null && world.getKey().equals(key)) return Optional.of(world);
        }

        if (record.worldName != null && !record.worldName.isBlank()) {
            world = Bukkit.getWorld(record.worldName);
            if (world != null && world.getName().equals(record.worldName)) return Optional.of(world);
        }
        return Optional.empty();
    }

    private Optional<Location> deathLocation(DeathRecord r) {
        return resolveWorld(r).map(w -> new Location(w, r.x, r.y, r.z, r.yaw, r.pitch));
    }

    private boolean beginChestSearch(DeathRecord record) {
        synchronized (record) {
            if (record.chestSearchInFlight || record.itemsResolved || record.isTerminal()) {
                plugin.debugDeath("chest search skipped deathId=" + record.deathId
                    + " inFlight=" + record.chestSearchInFlight + " itemsResolved=" + record.itemsResolved
                    + " terminal=" + record.isTerminal() + " stage=" + record.stage);
                return false;
            }
            record.chestSearchInFlight = true;
            plugin.debugDeath("chest search started deathId=" + record.deathId + " world=" + record.worldKey
                + " origin=" + record.x + "," + record.y + "," + record.z
                + " radius=" + chestSearchRadius + " emergencyRadius=" + emergencyChestSearchRadius);
            return true;
        }
    }

    private void endChestSearch(DeathRecord record) {
        synchronized (record) {
            record.chestSearchInFlight = false;
        }
        plugin.debugDeath("chest search ended deathId=" + record.deathId + " stage=" + record.stage
            + " chestCreated=" + record.chestCreated + " reserved=" + record.chestSpotReserved);
    }

    private void findAnyChestSpot(DeathRecord record, Consumer<Optional<ChestSpot>> callback) {
        findOrReuseChestSpot(record, nearby -> {
            if (nearby.isPresent() || resolveWorld(record).isEmpty()) {
                callback.accept(nearby);
                return;
            }
            findExpandedChestSpot(record, callback);
        });
    }

    private void findExpandedChestSpot(DeathRecord record, Consumer<Optional<ChestSpot>> callback) {
        Optional<World> worldOpt = resolveWorld(record);
        if (worldOpt.isEmpty()) {
            record.stage = Stage.WORLD_UNAVAILABLE;
            saveQuietly(record);
            callback.accept(Optional.empty());
            return;
        }
        World world = worldOpt.get();
        int originChunkX = ((int) Math.floor(record.x)) >> 4;
        int originChunkZ = ((int) Math.floor(record.z)) >> 4;
        int chunkRadius = Math.max(1, (emergencyChestSearchRadius + 15) / 16);
        List<ChunkCandidate> chunks = new ArrayList<>();
        for (int dx = -chunkRadius; dx <= chunkRadius; dx++) {
            for (int dz = -chunkRadius; dz <= chunkRadius; dz++) {
                int cx = originChunkX + dx;
                int cz = originChunkZ + dz;
                double centerX = cx * 16.0 + 7.5;
                double centerZ = cz * 16.0 + 7.5;
                double horizontalDistance = Math.hypot(centerX - record.x, centerZ - record.z);
                if (horizontalDistance <= emergencyChestSearchRadius + 12.0) {
                    chunks.add(new ChunkCandidate(cx, cz, horizontalDistance));
                }
            }
        }
        chunks.sort(Comparator.comparingDouble(c -> c.distance));
        inspectExpandedChestChunks(world, record, chunks, 0, callback);
    }

    private void inspectExpandedChestChunks(
        World world,
        DeathRecord record,
        List<ChunkCandidate> chunks,
        int index,
        Consumer<Optional<ChestSpot>> callback) {
        if (index >= chunks.size()) {
            findSpawnChestSpot(world, callback);
            return;
        }
        ChunkCandidate chunk = chunks.get(index);
        Bukkit.getRegionScheduler().execute(plugin, world, chunk.x, chunk.z, () -> {
            Optional<ChestSpot> found = scanChunkForChest(world, record, chunk.x, chunk.z);
            if (found.isPresent()) callback.accept(found);
            else inspectExpandedChestChunks(world, record, chunks, index + 1, callback);
        });
    }

    private void findSpawnChestSpot(World world, Consumer<Optional<ChestSpot>> callback) {
        Location spawn = world.getSpawnLocation();
        int chunkX = spawn.getBlockX() >> 4;
        int chunkZ = spawn.getBlockZ() >> 4;
        Bukkit.getRegionScheduler().execute(plugin, world, chunkX, chunkZ, () ->
            callback.accept(scanAnchorChunkForChest(world, spawn, chunkX, chunkZ)));
    }

    private Optional<ChestSpot> scanAnchorChunkForChest(World world, Location anchor, int chunkX, int chunkZ) {
        int minX = chunkX << 4;
        int minZ = chunkZ << 4;
        int maxX = minX + 15;
        int maxZ = minZ + 15;
        int originY = Math.max(world.getMinHeight(), Math.min(world.getMaxHeight() - 1, anchor.getBlockY()));

        for (int dy = 0; dy <= 32; dy++) {
            int low = originY - dy;
            if (low >= world.getMinHeight()) {
                Optional<ChestSpot> found = scanAnchorLayer(world, anchor, minX, maxX, minZ, maxZ, low);
                if (found.isPresent()) return found;
            }
            int high = originY + dy;
            if (dy > 0 && high < world.getMaxHeight()) {
                Optional<ChestSpot> found = scanAnchorLayer(world, anchor, minX, maxX, minZ, maxZ, high);
                if (found.isPresent()) return found;
            }
        }

        ChestSpot best = null;
        double bestDistance = Double.MAX_VALUE;
        for (int x = minX; x <= maxX; x++) {
            for (int z = minZ; z <= maxZ; z++) {
                int y = Math.max(world.getMinHeight(), Math.min(world.getMaxHeight() - 1, world.getHighestBlockYAt(x, z) + 1));
                Optional<ChestSpot> found = chestAt(world, x, y, z, maxX, maxZ);
                if (found.isEmpty()) continue;
                double distance = found.get().first.distanceSquared(anchor);
                if (distance < bestDistance) {
                    best = found.get();
                    bestDistance = distance;
                }
            }
        }
        return Optional.ofNullable(best);
    }

    private Optional<ChestSpot> scanAnchorLayer(
        World world, Location anchor, int minX, int maxX, int minZ, int maxZ, int y) {
        ChestSpot best = null;
        double bestDistance = Double.MAX_VALUE;
        for (int x = minX; x <= maxX; x++) {
            for (int z = minZ; z <= maxZ; z++) {
                Optional<ChestSpot> found = chestAt(world, x, y, z, maxX, maxZ);
                if (found.isEmpty()) continue;
                double distance = found.get().first.distanceSquared(anchor);
                if (distance < bestDistance) {
                    best = found.get();
                    bestDistance = distance;
                }
            }
        }
        return Optional.ofNullable(best);
    }

    private Optional<ChestSpot> scanChunkForChest(World world, DeathRecord record, int chunkX, int chunkZ) {
        int minX = chunkX << 4;
        int minZ = chunkZ << 4;
        int maxX = minX + 15;
        int maxZ = minZ + 15;
        int originY = Math.max(world.getMinHeight(), Math.min(world.getMaxHeight() - 1, (int) Math.floor(record.y)));
        double maxHorizontalDistanceSq = (double) emergencyChestSearchRadius * emergencyChestSearchRadius;

        // Search cave/air space close to the actual death height first. The old implementation
        // scanned the complete world height in every chunk and could stall a Folia region.
        int verticalRadius = Math.min(48, Math.max(originY - world.getMinHeight(), world.getMaxHeight() - 1 - originY));
        for (int dy = 0; dy <= verticalRadius; dy++) {
            int lowY = originY - dy;
            if (lowY >= world.getMinHeight()) {
                Optional<ChestSpot> found = scanChunkLayer(world, record, minX, maxX, minZ, maxZ, lowY, maxHorizontalDistanceSq);
                if (found.isPresent()) return found;
            }
            int highY = originY + dy;
            if (dy > 0 && highY < world.getMaxHeight()) {
                Optional<ChestSpot> found = scanChunkLayer(world, record, minX, maxX, minZ, maxZ, highY, maxHorizontalDistanceSq);
                if (found.isPresent()) return found;
            }
        }

        // If the death happened in a sealed cave/void, search the nearest terrain surface in the
        // same namespace world. This gives a real chest and coordinates instead of normal drops.
        ChestSpot best = null;
        double bestDistance = Double.MAX_VALUE;
        for (int x = minX; x <= maxX; x++) {
            for (int z = minZ; z <= maxZ; z++) {
                double dx = x + 0.5 - record.x;
                double dz = z + 0.5 - record.z;
                double horizontalDistance = dx * dx + dz * dz;
                if (horizontalDistance > maxHorizontalDistanceSq) continue;

                int y = Math.max(world.getMinHeight(), Math.min(world.getMaxHeight() - 1, world.getHighestBlockYAt(x, z) + 1));
                double dy = y - record.y;
                double distance = horizontalDistance + dy * dy;
                if (distance >= bestDistance) continue;
                Optional<ChestSpot> found = chestAt(world, x, y, z, maxX, maxZ);
                if (found.isPresent()) {
                    best = found.get();
                    bestDistance = distance;
                }
            }
        }
        return Optional.ofNullable(best);
    }

    private Optional<ChestSpot> scanChunkLayer(
        World world,
        DeathRecord record,
        int minX,
        int maxX,
        int minZ,
        int maxZ,
        int y,
        double maxHorizontalDistanceSq) {
        ChestSpot best = null;
        double bestDistance = Double.MAX_VALUE;
        for (int x = minX; x <= maxX; x++) {
            for (int z = minZ; z <= maxZ; z++) {
                double dx = x + 0.5 - record.x;
                double dy = y - record.y;
                double dz = z + 0.5 - record.z;
                double horizontalDistance = dx * dx + dz * dz;
                if (horizontalDistance > maxHorizontalDistanceSq) continue;
                double distance = horizontalDistance + dy * dy;
                if (distance >= bestDistance) continue;

                Optional<ChestSpot> found = chestAt(world, x, y, z, maxX, maxZ);
                if (found.isPresent()) {
                    best = found.get();
                    bestDistance = distance;
                }
            }
        }
        return Optional.ofNullable(best);
    }

    private Optional<ChestSpot> chestAt(World world, int x, int y, int z, int maxX, int maxZ) {
        Block first = world.getBlockAt(x, y, z);
        if (!replaceable(first)) return Optional.empty();
        if (x < maxX) {
            Block east = world.getBlockAt(x + 1, y, z);
            if (replaceable(east)) return Optional.of(new ChestSpot(first.getLocation(), east.getLocation(), true));
        }
        if (z < maxZ) {
            Block south = world.getBlockAt(x, y, z + 1);
            if (replaceable(south)) return Optional.of(new ChestSpot(first.getLocation(), south.getLocation(), false));
        }
        return Optional.empty();
    }

    private void findOrReuseChestSpot(DeathRecord record, Consumer<Optional<ChestSpot>> callback) {
        if (!record.chestSpotReserved) {
            findChestSpot(record, callback);
            return;
        }

        Optional<World> worldOpt = resolveWorld(record);
        if (worldOpt.isEmpty()) {
            record.stage = Stage.WORLD_UNAVAILABLE;
            saveQuietly(record);
            callback.accept(Optional.empty());
            return;
        }

        World world = worldOpt.get();
        Location first = new Location(world, record.chestX, record.chestY, record.chestZ);
        Location second = new Location(world, record.chestSecondX, record.chestSecondY, record.chestSecondZ);
        int dx = second.getBlockX() - first.getBlockX();
        int dz = second.getBlockZ() - first.getBlockZ();
        if (first.getBlockY() < world.getMinHeight() || first.getBlockY() >= world.getMaxHeight()
            || second.getBlockY() < world.getMinHeight() || second.getBlockY() >= world.getMaxHeight()
            || first.getBlockY() != second.getBlockY() || Math.abs(dx) + Math.abs(dz) != 1
            || (first.getBlockX() >> 4) != (second.getBlockX() >> 4)
            || (first.getBlockZ() >> 4) != (second.getBlockZ() >> 4)) {
            record.chestSpotReserved = false;
            saveQuietly(record);
            findChestSpot(record, callback);
            return;
        }

        ChestSpot reserved = new ChestSpot(first, second, dx != 0);
        Bukkit.getRegionScheduler().execute(plugin, world, first.getBlockX() >> 4, first.getBlockZ() >> 4, () -> {
            Block a = first.getBlock();
            Block b = second.getBlock();
            boolean usable = (replaceable(a) || isOwnedChest(a, record.deathId))
                && (replaceable(b) || isOwnedChest(b, record.deathId));
            if (usable) {
                callback.accept(Optional.of(reserved));
                return;
            }

            record.chestSpotReserved = false;
            saveQuietly(record);
            findChestSpot(record, callback);
        });
    }

    private void reserveChestSpot(DeathRecord record, ChestSpot spot) {
        record.chestX = spot.first.getBlockX();
        record.chestY = spot.first.getBlockY();
        record.chestZ = spot.first.getBlockZ();
        record.chestSecondX = spot.second.getBlockX();
        record.chestSecondY = spot.second.getBlockY();
        record.chestSecondZ = spot.second.getBlockZ();
        record.chestSpotReserved = true;
        plugin.debugDeath("chest spot reserved deathId=" + record.deathId + " first=" + record.chestX + ","
            + record.chestY + "," + record.chestZ + " second=" + record.chestSecondX + ","
            + record.chestSecondY + "," + record.chestSecondZ);
        saveQuietly(record);
    }

    private void findChestSpot(DeathRecord record, Consumer<Optional<ChestSpot>> callback) {
        Optional<World> worldOpt = resolveWorld(record);
        if (worldOpt.isEmpty()) {
            record.stage = Stage.WORLD_UNAVAILABLE;
            saveQuietly(record);
            callback.accept(Optional.empty());
            return;
        }
        World world = worldOpt.get();
        int ox = (int) Math.floor(record.x), oy = (int) Math.floor(record.y), oz = (int) Math.floor(record.z);
        int minY = Math.max(world.getMinHeight(), oy - chestSearchRadius);
        int maxY = Math.min(world.getMaxHeight() - 1, oy + chestSearchRadius);
        List<ChestSpot> candidates = new ArrayList<>();
        for (int y = minY; y <= maxY; y++) {
            int dy = y - oy;
            for (int dx = -chestSearchRadius; dx <= chestSearchRadius; dx++) {
                for (int dz = -chestSearchRadius; dz <= chestSearchRadius; dz++) {
                    if (dx * dx + dy * dy + dz * dz > chestSearchRadius * chestSearchRadius) continue;
                    Location a = new Location(world, ox + dx, y, oz + dz);
                    Location east = a.clone().add(1, 0, 0);
                    Location south = a.clone().add(0, 0, 1);
                    if ((a.getBlockX() >> 4) == (east.getBlockX() >> 4) && (a.getBlockZ() >> 4) == (east.getBlockZ() >> 4))
                        candidates.add(new ChestSpot(a, east, true));
                    if ((a.getBlockX() >> 4) == (south.getBlockX() >> 4) && (a.getBlockZ() >> 4) == (south.getBlockZ() >> 4))
                        candidates.add(new ChestSpot(a, south, false));
                }
            }
        }
        candidates.sort(Comparator.comparingDouble(s -> s.first.distanceSquared(new Location(world, record.x, record.y, record.z))));
        Map<Long, List<ChestSpot>> chunks = new LinkedHashMap<>();
        for (ChestSpot spot : candidates) {
            long key = (((long) spot.first.getBlockX() >> 4) << 32) ^ (((long) spot.first.getBlockZ() >> 4) & 0xffffffffL);
            chunks.computeIfAbsent(key, ignored -> new ArrayList<>()).add(spot);
        }
        inspectChestChunks(world, new ArrayList<>(chunks.values()), 0, callback);
    }

    private void inspectChestChunks(World world, List<List<ChestSpot>> groups, int index, Consumer<Optional<ChestSpot>> callback) {
        if (index >= groups.size()) { callback.accept(Optional.empty()); return; }
        List<ChestSpot> group = groups.get(index);
        ChestSpot first = group.get(0);
        int cx = first.first.getBlockX() >> 4, cz = first.first.getBlockZ() >> 4;
        Bukkit.getRegionScheduler().execute(plugin, world, cx, cz, () -> {
            for (ChestSpot spot : group) {
                Block a = spot.first.getBlock(), b = spot.second.getBlock();
                if (replaceable(a) && replaceable(b)) { callback.accept(Optional.of(spot)); return; }
            }
            inspectChestChunks(world, groups, index + 1, callback);
        });
    }

    private boolean replaceable(Block block) {
        Material type = block.getType();
        return type.isAir() || type == Material.WATER || type == Material.LAVA;
    }

    private boolean isOwnedChest(Block block, UUID deathId) {
        if (block.getType() != Material.CHEST) return false;
        BlockState state = block.getState();
        if (!(state instanceof org.bukkit.block.TileState tile)) return false;
        String marker = tile.getPersistentDataContainer().get(deathIdKey, PersistentDataType.STRING);
        return deathId.toString().equals(marker);
    }

    private String chestState(Block block) {
        BlockState state = block.getState();
        if (!(state instanceof org.bukkit.block.TileState tile)) return "";
        String value = tile.getPersistentDataContainer().get(chestStateKey, PersistentDataType.STRING);
        return value == null ? "" : value;
    }

    private void markChestState(Block a, Block b, DeathRecord record, String state) {
        BlockState firstState = a.getState();
        BlockState secondState = b.getState();
        if (!(firstState instanceof org.bukkit.block.TileState first)
            || !(secondState instanceof org.bukkit.block.TileState second)) {
            throw new IllegalStateException("Death chest blocks are not tile states");
        }
        first.getPersistentDataContainer().set(deathIdKey, PersistentDataType.STRING, record.deathId.toString());
        second.getPersistentDataContainer().set(deathIdKey, PersistentDataType.STRING, record.deathId.toString());
        first.getPersistentDataContainer().set(chestStateKey, PersistentDataType.STRING, state);
        second.getPersistentDataContainer().set(chestStateKey, PersistentDataType.STRING, state);
        first.update(true, false);
        second.update(true, false);
    }

    private void configureDoubleChest(Block a, Block b, boolean eastWest) {
        a.setType(Material.CHEST, false);
        b.setType(Material.CHEST, false);
        Chest da = (Chest) Material.CHEST.createBlockData();
        Chest db = (Chest) Material.CHEST.createBlockData();
        if (eastWest) {
            da.setFacing(BlockFace.NORTH);
            db.setFacing(BlockFace.NORTH);
        } else {
            da.setFacing(BlockFace.EAST);
            db.setFacing(BlockFace.EAST);
        }
        da.setType(Chest.Type.LEFT);
        db.setType(Chest.Type.RIGHT);
        a.setBlockData(da, false);
        b.setBlockData(db, false);
    }

    private void clearContainer(Block block) {
        BlockState state = block.getState();
        if (state instanceof Container container) container.getInventory().clear();
    }

    private void removeOwnedChest(Block block, UUID deathId) {
        if (isOwnedChest(block, deathId)) block.setType(Material.AIR, false);
    }

    private void placeChest(DeathRecord record, ChestSpot spot, Consumer<Boolean> callback) {
        World world = spot.first.getWorld();
        plugin.debugDeath("queue chest placement deathId=" + record.deathId + " world=" + world.getKey()
            + " first=" + spot.first.getBlockX() + "," + spot.first.getBlockY() + "," + spot.first.getBlockZ()
            + " second=" + spot.second.getBlockX() + "," + spot.second.getBlockY() + "," + spot.second.getBlockZ());
        Bukkit.getRegionScheduler().execute(plugin, world, spot.first.getBlockX() >> 4, spot.first.getBlockZ() >> 4, () -> {
            Block a = spot.first.getBlock();
            Block b = spot.second.getBlock();
            try {
                boolean ownedA = isOwnedChest(a, record.deathId);
                boolean ownedB = isOwnedChest(b, record.deathId);
                if (ownedA && ownedB
                    && "complete".equalsIgnoreCase(chestState(a))
                    && "complete".equalsIgnoreCase(chestState(b))) {
                    record.chestSpotReserved = true;
                    record.chestCreated = true;
                    record.itemsResolved = true;
                    saveQuietly(record);
                    plugin.debugDeath("existing complete chest reused deathId=" + record.deathId);
                    callback.accept(true);
                    return;
                }

                if ((!ownedA && !replaceable(a)) || (!ownedB && !replaceable(b))) {
                    plugin.debugDeath("chest placement blocked deathId=" + record.deathId
                        + " firstType=" + a.getType() + " secondType=" + b.getType());
                    callback.accept(false);
                    return;
                }

                configureDoubleChest(a, b, spot.eastWest);
                markChestState(a, b, record, "filling");
                clearContainer(a);
                clearContainer(b);

                List<ItemStack> items = deserializeItems(record.itemsBase64);
                int cursor = fillContainer(a.getState(), items, 0);
                if (cursor < items.size()) cursor = fillContainer(b.getState(), items, cursor);
                if (cursor < items.size()) {
                    removeOwnedChest(a, record.deathId);
                    removeOwnedChest(b, record.deathId);
                    callback.accept(false);
                    return;
                }

                markChestState(a, b, record, "complete");
                record.chestSpotReserved = true;
                record.chestCreated = true;
                record.itemsResolved = true;
                plugin.debugDeath("chest placement completed deathId=" + record.deathId + " itemStacks=" + items.size()
                    + " first=" + a.getX() + "," + a.getY() + "," + a.getZ());
                saveQuietly(record);
                callback.accept(true);
            } catch (Exception ex) {
                plugin.getLogger().log(Level.SEVERE, "Failed to create death chest " + record.deathId, ex);
                try {
                    removeOwnedChest(a, record.deathId);
                    removeOwnedChest(b, record.deathId);
                } catch (RuntimeException cleanupError) {
                    plugin.getLogger().log(Level.SEVERE, "Failed to clean incomplete death chest " + record.deathId, cleanupError);
                }
                callback.accept(false);
            }
        });
    }

    private int fillContainer(BlockState state, List<ItemStack> items, int cursor) {
        if (!(state instanceof Container container)) return cursor;
        Inventory inventory = container.getInventory();
        while (cursor < items.size()) {
            Map<Integer, ItemStack> left = inventory.addItem(items.get(cursor).clone());
            if (!left.isEmpty()) break;
            cursor++;
        }
        return cursor;
    }

    private void createFreeChest(DeathRecord record, String reason) {
        createGuaranteedChest(record, reason, true);
    }

    private void createCompensatedChest(DeathRecord record, String reason) {
        createGuaranteedChest(record, reason, false);
    }

    private void createGuaranteedChest(DeathRecord record, String reason, boolean backendOutage) {
        plugin.debugDeath("guaranteed chest requested deathId=" + record.deathId + " backendOutage=" + backendOutage
            + " reason=" + reason + " stage=" + record.stage + " itemsResolved=" + record.itemsResolved);
        synchronized (record) {
            if (record.itemsResolved || record.isTerminal()) return;
            record.stage = Stage.RESOLVING;
            record.backendUnavailable |= backendOutage;
            saveQuietly(record);
        }
        if (!beginChestSearch(record)) return;
        if (deserializeItems(record.itemsBase64).size() > 54) {
            endChestSearch(record);
            scheduleGuaranteedChestRetry(record,
                reason + " Вещей больше вместимости двойного сундука; они остаются в защищённом хранилище плагина.");
            return;
        }
        findAnyChestSpot(record, spot -> {
            if (spot.isEmpty()) {
                plugin.debugDeath("no chest spot found deathId=" + record.deathId + " world=" + record.worldKey);
                endChestSearch(record);
                scheduleGuaranteedChestRetry(record, reason + " Подходящее место пока не найдено.");
                return;
            }
            reserveChestSpot(record, spot.get());
            placeChest(record, spot.get(), placed -> {
                endChestSearch(record);
                if (placed) {
                    plugin.debugDeath("guaranteed chest resolved deathId=" + record.deathId + " backendOutage=" + backendOutage);
                    record.offerClosed = true;
                    notifyChest(record, spot.get().first, backendOutage, reason);
                    finish(record, Stage.FREE_CHEST_CREATED);
                } else {
                    record.chestSpotReserved = false;
                    saveQuietly(record);
                    scheduleGuaranteedChestRetry(record, reason + " Место заняли; ищем следующее.");
                }
            });
        });
    }

    private void scheduleGuaranteedChestRetry(DeathRecord record, String message) {
        boolean notify;
        synchronized (record) {
            if (record.itemsResolved || record.isTerminal()) return;
            notify = !"death-chest-retry".equals(record.lastError);
            record.stage = resolveWorld(record).isEmpty() ? Stage.WORLD_UNAVAILABLE : Stage.RESOLVING;
            record.chestSpotReserved = false;
            record.lastError = "death-chest-retry";
            record.retryAfter = Instant.now().plusSeconds(15);
            saveQuietly(record);
        }
        plugin.debugDeath("guaranteed chest retry scheduled deathId=" + record.deathId
            + " retryAfter=" + record.retryAfter + " notifyPlayer=" + notify + " message=" + message);
        if (notify) {
            Player player = plugin.findOnlinePlayer(record.playerId);
            if (player != null && player.isOnline()) {
                onPlayer(player, () -> player.sendMessage(Component.text(message, NamedTextColor.YELLOW)), 1L);
            }
        }
    }

    private void fallbackUnclaimedOffer(DeathRecord record, String reason) {
        synchronized (record) {
            if (record.itemsResolved || record.isTerminal()
                || (record.stage != Stage.WAITING_RESPAWN && record.stage != Stage.OFFER)) return;
            record.backendUnavailable = true;
            record.action = "backend-fallback";
            record.stage = Stage.RESOLVING;
        }
        saveQuietly(record);
        createFreeChest(record, reason);
    }

    private void notifyDeathCoordinates(DeathRecord record) {
        Player player = plugin.findOnlinePlayer(record.playerId);
        synchronized (record) {
            if (record.coordinatesNotified || record.coordinatesNotificationDispatchPending) return;
            if (player == null || !player.isOnline() || player.isDead()) {
                plugin.debugDeath("coordinates notification deferred deathId=" + record.deathId
                    + " playerPresent=" + (player != null) + " online=" + (player != null && player.isOnline())
                    + " dead=" + (player != null && player.isDead()));
                return;
            }
            record.coordinatesNotificationDispatchPending = true;
        }
        onPlayer(player, () -> {
            synchronized (record) { record.coordinatesNotificationDispatchPending = false; }
            if (!player.isOnline() || player.isDead()) {
                plugin.debugDeath("coordinates notification scheduler ran but player unavailable deathId=" + record.deathId);
                return;
            }
            int x = (int) Math.floor(record.x);
            int y = (int) Math.floor(record.y);
            int z = (int) Math.floor(record.z);
            player.sendMessage(Component.text("Координаты получены. Списано " + coordinatesCost + ".", NamedTextColor.GREEN));
            player.sendMessage(Component.text("Место смерти: " + record.worldKey + " / " + record.worldName
                + " — " + x + " " + y + " " + z, NamedTextColor.LIGHT_PURPLE));
            synchronized (record) {
                record.coordinatesGranted = true;
                record.coordinatesNotified = true;
                record.updatedAt = Instant.now();
                saveQuietly(record);
            }
            plugin.debugDeath("coordinates notification delivered deathId=" + record.deathId + " player=" + record.playerId);
        }, () -> {
            synchronized (record) { record.coordinatesNotificationDispatchPending = false; }
            plugin.debugDeath("coordinates notification scheduler retired deathId=" + record.deathId);
        }, 1L);
    }

    private void notifyFreeChest(DeathRecord record, Location at, String reason) {
        notifyChest(record, at, true, reason);
    }

    private void notifyChest(DeathRecord record, Location at, boolean free, String reason) {
        Player player = plugin.findOnlinePlayer(record.playerId);
        synchronized (record) {
            if (record.chestNotified || record.chestNotificationDispatchPending) return;
            if (player == null || !player.isOnline() || player.isDead()) {
                plugin.debugDeath("chest notification deferred deathId=" + record.deathId
                    + " playerPresent=" + (player != null) + " online=" + (player != null && player.isOnline())
                    + " dead=" + (player != null && player.isDead()) + " chest=" + record.worldKey + " "
                    + at.getBlockX() + "," + at.getBlockY() + "," + at.getBlockZ());
                return;
            }
            record.chestNotificationDispatchPending = true;
        }
        onPlayer(player, () -> {
            synchronized (record) { record.chestNotificationDispatchPending = false; }
            if (!player.isOnline() || player.isDead()) {
                plugin.debugDeath("chest notification scheduler ran but player unavailable deathId=" + record.deathId);
                return;
            }
            if (free) {
                player.showTitle(Title.title(Component.text("Сервис рейтинга недоступен", NamedTextColor.YELLOW),
                    Component.text("Вещи бесплатно сохранены в сундуке", NamedTextColor.GREEN)));
            }
            player.sendMessage(Component.text(reason, free ? NamedTextColor.YELLOW : NamedTextColor.GREEN));
            player.sendMessage(Component.text("Сундук: " + record.worldKey + " / " + record.worldName + " "
                + at.getBlockX() + " " + at.getBlockY() + " " + at.getBlockZ(), NamedTextColor.AQUA));
            synchronized (record) {
                record.chestNotified = true;
                record.userNotified = record.coordinatesNotified && record.chestNotified;
                record.updatedAt = Instant.now();
                saveQuietly(record);
            }
            plugin.debugDeath("chest notification delivered deathId=" + record.deathId + " player=" + record.playerId
                + " free=" + free + " chest=" + record.worldKey + " " + at.getBlockX() + "," + at.getBlockY() + "," + at.getBlockZ());
        }, () -> {
            synchronized (record) { record.chestNotificationDispatchPending = false; }
            plugin.debugDeath("chest notification scheduler retired deathId=" + record.deathId);
        }, 1L);
    }

    private void notifyUnseenChests(Player player) {
        for (DeathRecord record : new ArrayList<>(deaths.values())) {
            if (!record.playerId.equals(player.getUniqueId()) || !record.chestCreated || record.chestNotified) continue;
            Optional<World> world = resolveWorld(record);
            if (world.isEmpty()) continue;
            Location at = new Location(world.get(), record.chestX, record.chestY, record.chestZ);
            boolean free = record.backendUnavailable || record.compensated;
            notifyChest(record, at, free, free
                ? "Сервис рейтинга был недоступен — вещи бесплатно сохранены в сундуке."
                : "Сундук смерти создан.");
        }
    }

    private void releaseDrops(DeathRecord record, String message) { releaseDrops(record, message, null); }

    private void releaseDrops(DeathRecord record, String message, Runnable after) {
        Optional<Location> locOpt = deathLocation(record);
        if (locOpt.isEmpty()) {
            record.stage = Stage.WORLD_UNAVAILABLE;
            saveQuietly(record);
            Player player = plugin.findOnlinePlayer(record.playerId);
            if (player != null) onPlayer(player, () -> player.sendMessage(Component.text(
                "Мир смерти пока не загружен. Вещи сохранены и будут обработаны позже.", NamedTextColor.YELLOW)), 1L);
            return;
        }
        Location at = locOpt.get(); World world = at.getWorld();
        Bukkit.getRegionScheduler().execute(plugin, world, at.getBlockX() >> 4, at.getBlockZ() >> 4, () -> {
            try {
                List<ItemStack> items = deserializeItems(record.itemsBase64);
                Map<Integer, Boolean> existing = new HashMap<>();
                // Keep all idempotency checks inside the exact chunk owned by this RegionScheduler task.
                // Items are dropped at the exact location (not randomly scattered across a chunk border).
                for (org.bukkit.entity.Entity entity : world.getChunkAt(at.getBlockX() >> 4, at.getBlockZ() >> 4).getEntities()) {
                    if (!(entity instanceof Item item)) continue;
                    String id = item.getPersistentDataContainer().get(deathIdKey, PersistentDataType.STRING);
                    Integer idx = item.getPersistentDataContainer().get(itemIndexKey, PersistentDataType.INTEGER);
                    if (record.deathId.toString().equals(id) && idx != null) existing.put(idx, true);
                }
                for (int i = 0; i < items.size(); i++) {
                    if (existing.containsKey(i)) continue;
                    Item entity = world.dropItem(at, items.get(i));
                    entity.getPersistentDataContainer().set(deathIdKey, PersistentDataType.STRING, record.deathId.toString());
                    entity.getPersistentDataContainer().set(itemIndexKey, PersistentDataType.INTEGER, i);
                }
                record.itemsResolved = true; record.dropsReleased = true;
                finish(record, Stage.DROPS_RELEASED);
                Player player = plugin.findOnlinePlayer(record.playerId);
                if (message != null && player != null) onPlayer(player, () -> player.sendMessage(Component.text(message, NamedTextColor.YELLOW)), 1L);
                if (after != null) after.run();
            } catch (Exception ex) {
                plugin.getLogger().log(Level.SEVERE, "Failed to release death drops " + record.deathId, ex);
                record.stage = Stage.RESOLVING;
                saveQuietly(record);
            }
        });
    }

    private void findSafeLocation(DeathRecord record, Location selected, Consumer<Optional<Location>> callback) {
        World world = selected.getWorld();
        if (world == null || !world.getUID().equals(record.worldUuid)) { callback.accept(Optional.empty()); return; }
        List<Location> candidates = new ArrayList<>();
        int ox = selected.getBlockX(), oy = selected.getBlockY(), oz = selected.getBlockZ();
        for (int r = 0; r <= safeSearchRadius; r++) {
            for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) for (int dz = -r; dz <= r; dz++) {
                if (Math.max(Math.max(Math.abs(dx), Math.abs(dy)), Math.abs(dz)) != r) continue;
                int y = oy + dy;
                if (y <= world.getMinHeight() || y >= world.getMaxHeight() - 1) continue;
                candidates.add(new Location(world, ox + dx + 0.5, y, oz + dz + 0.5, selected.getYaw(), selected.getPitch()));
            }
        }
        Map<Long, List<Location>> byChunk = new LinkedHashMap<>();
        for (Location l : candidates) {
            long key = (((long) l.getBlockX() >> 4) << 32) ^ (((long) l.getBlockZ() >> 4) & 0xffffffffL);
            byChunk.computeIfAbsent(key, ignored -> new ArrayList<>()).add(l);
        }
        inspectSafeChunks(world, new ArrayList<>(byChunk.values()), 0, callback);
    }

    private void inspectSafeChunks(World world, List<List<Location>> groups, int index, Consumer<Optional<Location>> callback) {
        if (index >= groups.size()) { callback.accept(Optional.empty()); return; }
        Location sample = groups.get(index).get(0);
        Bukkit.getRegionScheduler().execute(plugin, world, sample.getBlockX() >> 4, sample.getBlockZ() >> 4, () -> {
            for (Location l : groups.get(index)) {
                Block feet = l.getBlock(), head = feet.getRelative(BlockFace.UP), floor = feet.getRelative(BlockFace.DOWN);
                boolean open = isSafeBodyBlock(feet) && isSafeBodyBlock(head);
                boolean safeFloor = isSafeFloor(floor);
                if (open && safeFloor) {
                    callback.accept(Optional.of(l)); return;
                }
            }
            inspectSafeChunks(world, groups, index + 1, callback);
        });
    }

    private boolean isSafeBodyBlock(Block block) {
        Material type = block.getType();
        if (!(type.isAir() || block.isPassable())) return false;
        return type != Material.WATER
            && type != Material.LAVA
            && type != Material.FIRE
            && type != Material.SOUL_FIRE
            && type != Material.POWDER_SNOW
            && type != Material.COBWEB
            && type != Material.SWEET_BERRY_BUSH
            && type != Material.WITHER_ROSE
            && type != Material.POINTED_DRIPSTONE;
    }

    private boolean isSafeFloor(Block block) {
        Material type = block.getType();
        return type.isSolid()
            && type != Material.MAGMA_BLOCK
            && type != Material.CAMPFIRE
            && type != Material.SOUL_CAMPFIRE
            && type != Material.CACTUS
            && type != Material.POWDER_SNOW
            && type != Material.POINTED_DRIPSTONE;
    }

    private CompletableFuture<PurchaseResult> purchase(DeathRecord record, String action, int amount) {
        String actionRequestId = actionRequestId(record, action);
        String body = json(Map.of(
            "playerUuid", record.playerId.toString(),
            "playerName", record.playerName,
            "requestId", actionRequestId,
            "action", action,
            "amount", amount));
        plugin.debugDeath("purchase started deathId=" + record.deathId + " action=" + action
            + " amount=" + amount + " requestId=" + actionRequestId);
        return request("POST", API_PATH + "/" + record.deathId + "/purchase", body).thenApply(response -> {
            if (response == null || response.statusCode() >= 500) return PurchaseResult.unavailable("backend-unavailable");
            String reason = "";
            int balance = -1;
            int cost = amount;
            int charged = amount;
            try {
                JsonObject payload = response.body() == null || response.body().isBlank()
                    ? new JsonObject() : JsonParser.parseString(response.body()).getAsJsonObject();
                if (payload.has("revision")) {
                    synchronized (record) { record.revision = Math.max(record.revision, payload.get("revision").getAsLong()); }
                }
                if (payload.has("reason")) reason = payload.get("reason").getAsString();
                if (payload.has("balance")) balance = payload.get("balance").getAsInt();
                if (payload.has("cost")) cost = payload.get("cost").getAsInt();
                if (payload.has("charged")) charged = payload.get("charged").getAsInt();
                if (response.statusCode() >= 200 && response.statusCode() < 300
                    && payload.has("success") && payload.get("success").getAsBoolean()) {
                    plugin.debugDeath("purchase confirmed deathId=" + record.deathId + " action=" + action
                        + " amount=" + amount + " requestId=" + actionRequestId
                        + " duplicate=" + (payload.has("duplicate") && payload.get("duplicate").getAsBoolean()));
                    return PurchaseResult.confirmed(charged > 0 ? charged : amount);
                }
            } catch (RuntimeException ex) {
                plugin.getLogger().log(Level.WARNING, "Cannot parse purchase response deathId=" + record.deathId
                    + " action=" + action + " status=" + response.statusCode(), ex);
                if (response.statusCode() >= 200 && response.statusCode() < 300) return PurchaseResult.unavailable("invalid-response");
            }
            if (response.statusCode() == 409 && "not-linked".equals(reason)) return PurchaseResult.unlinked(reason);
            plugin.debugDeath("purchase denied deathId=" + record.deathId + " action=" + action
                + " status=" + response.statusCode() + " reason=" + reason + " balance=" + balance + " cost=" + cost);
            return PurchaseResult.denied(reason.isBlank() ? "purchase-denied" : reason, balance, cost);
        }).exceptionally(ex -> PurchaseResult.unavailable("request-failed"));
    }

    private String actionRequestId(DeathRecord record, String action) {
        return "death:" + record.deathId + ":" + action;
    }

    private CompletableFuture<BackendResult> syncCreate(DeathRecord record) {
        String body = recordJson(record);
        return request("PUT", API_PATH + "/" + record.deathId, body).thenApply(response -> {
            if (response == null || response.statusCode() >= 500) return BackendResult.UNAVAILABLE;
            if (response.statusCode() == 409 && response.body() != null
                && response.body().toLowerCase(Locale.ROOT).contains("not-linked")) {
                return BackendResult.UNLINKED;
            }
            if (response.statusCode() >= 200 && response.statusCode() < 300) {
                try {
                    JsonObject payload = JsonParser.parseString(response.body()).getAsJsonObject();
                    if (payload.has("revision")) {
                        synchronized (record) {
                            record.revision = Math.max(record.revision, payload.get("revision").getAsLong());
                        }
                    }
                } catch (RuntimeException ignored) { }
                return BackendResult.AVAILABLE;
            }
            return response.statusCode() < 500 ? BackendResult.AVAILABLE : BackendResult.UNAVAILABLE;
        }).exceptionally(ex -> BackendResult.UNAVAILABLE);
    }

    private void syncState(DeathRecord record) {
        request("PUT", API_PATH + "/" + record.deathId, recordJson(record)).exceptionally(ex -> null);
    }

    private CompletableFuture<Boolean> health(UUID playerUuid) {
        if (apiBaseUrl.isBlank()) return CompletableFuture.completedFuture(false);
        String suffix = playerUuid == null ? "" : "?playerUuid=" + playerUuid;
        return request("GET", API_PATH + "/health" + suffix, null)
            .thenApply(r -> {
                if (r == null || r.statusCode() < 200 || r.statusCode() >= 300) return false;
                try {
                    JsonObject payload = JsonParser.parseString(r.body()).getAsJsonObject();
                    return payload.has("available") && payload.get("available").getAsBoolean();
                } catch (RuntimeException ex) {
                    return false;
                }
            })
            .exceptionally(ex -> false);
    }

    private void requestCompensation(DeathRecord record, String action, int amount) {
        String sourceRequestId = actionRequestId(record, action);
        synchronized (record) {
            record.compensationAction = action;
            record.compensationAmount = amount;
            record.compensationPending = true;
            record.compensationRetryAfter = Instant.now().plusSeconds(15);
        }
        saveQuietly(record);
        String body = json(Map.of(
            "requestId", sourceRequestId,
            "amount", amount,
            "reason", "fulfillment-failed:" + action));
        plugin.debugDeath("compensation requested deathId=" + record.deathId + " action=" + action
            + " amount=" + amount + " requestId=" + sourceRequestId);
        request("POST", API_PATH + "/" + record.deathId + "/compensate", body).thenAccept(response -> {
            if (response == null || response.statusCode() < 200 || response.statusCode() >= 300) return;
            try {
                JsonObject payload = JsonParser.parseString(response.body()).getAsJsonObject();
                if (!payload.has("success") || !payload.get("success").getAsBoolean()) return;
                boolean noCharge = payload.has("noCharge") && payload.get("noCharge").getAsBoolean();
                int refunded = payload.has("refunded") ? payload.get("refunded").getAsInt() : 0;
                synchronized (record) {
                    record.compensationPending = false;
                    record.compensationAction = "";
                    record.compensationAmount = 0;
                    if (!noCharge && refunded > 0) record.chargedAmount = Math.max(0, record.chargedAmount - refunded);
                    record.compensated = record.chargedAmount == 0;
                    if (payload.has("revision")) record.revision = Math.max(record.revision, payload.get("revision").getAsLong());
                }
                plugin.debugDeath("compensation completed deathId=" + record.deathId + " action=" + action
                    + " noCharge=" + noCharge + " refunded=" + refunded + " remainingCharged=" + record.chargedAmount);
                saveQuietly(record);
            } catch (RuntimeException ex) {
                plugin.getLogger().log(Level.WARNING, "Cannot parse death compensation response", ex);
            }
        }).exceptionally(ex -> null);
    }

    private CompletableFuture<HttpResponse<String>> request(String method, String path, String body) {
        if (apiBaseUrl.isBlank()) {
            plugin.debugDeath("backend request blocked because apiBaseUrl is empty method=" + method + " path=" + path);
            return CompletableFuture.failedFuture(new IOException("Minecraft API URL is not configured"));
        }
        long requestNumber = backendRequestSequence.incrementAndGet();
        URI uri = URI.create(trimSlash(apiBaseUrl) + path);
        String requestId = UUID.randomUUID().toString();
        HttpRequest.Builder builder = HttpRequest.newBuilder(uri)
            .timeout(backendTimeout)
            .header("Accept", "application/json")
            .header("User-Agent", "TaskForgeLink/" + plugin.getDescription().getVersion())
            .header("X-Request-Id", requestId);
        if (!serverToken.isBlank()) builder.header("X-Minecraft-Key", serverToken);
        if (body == null) builder.method(method, HttpRequest.BodyPublishers.noBody());
        else builder.header("Content-Type", "application/json").method(method, HttpRequest.BodyPublishers.ofString(body));
        HttpRequest request = builder.build();
        plugin.debugDeath("backend-http #" + requestNumber + " -> " + method + " " + uri
            + " requestId=" + requestId + " tokenPresent=" + !serverToken.isBlank()
            + " tokenLen=" + serverToken.length() + " bodyLen=" + (body == null ? 0 : body.length()));
        return http.sendAsync(request, HttpResponse.BodyHandlers.ofString())
            .whenComplete((response, error) -> {
                if (error != null) {
                    plugin.getLogger().log(Level.WARNING, "[DEBUG][death-http] #" + requestNumber + " !! "
                        + method + " " + uri + " requestId=" + requestId + " "
                        + error.getClass().getName() + ": " + error.getMessage(), error);
                    return;
                }
                String responseBody = response == null || response.body() == null ? "" : response.body().replace('\n', ' ').replace('\r', ' ');
                if (responseBody.length() > 1000) responseBody = responseBody.substring(0, 1000) + "...<truncated>";
                plugin.debugDeath("backend-http #" + requestNumber + " <- requestId=" + requestId + " status="
                    + (response == null ? "null" : response.statusCode()) + " body=" + (responseBody.isEmpty() ? "<empty>" : responseBody));
            });
    }

    private void finish(DeathRecord record, Stage stage) {
        record.stage = stage; record.updatedAt = Instant.now(); saveQuietly(record);
        if (record.isTerminal()) {
            // Retain the small journal until backend acknowledges state; old completed records are pruned on load.
        }
    }

    private void onPlayer(Player player, Runnable action, long delay) {
        onPlayer(player, action, () -> plugin.debugScheduler("entity scheduler retired player="
            + player.getUniqueId() + " delayTicks=" + delay), delay);
    }

    private void onPlayer(Player player, Runnable action, Runnable retired, long delay) {
        plugin.debugScheduler("queue entity task player=" + player.getName() + "/" + player.getUniqueId()
            + " dead=" + player.isDead() + " online=" + player.isOnline() + " delayTicks=" + delay);
        player.getScheduler().execute(plugin, () -> {
            plugin.debugScheduler("run entity task player=" + player.getName() + "/" + player.getUniqueId()
                + " dead=" + player.isDead() + " online=" + player.isOnline());
            try {
                action.run();
            } catch (Throwable error) {
                plugin.getLogger().log(Level.SEVERE, "Entity-scheduled death recovery action failed for " + player.getUniqueId(), error);
            }
        }, retired, delay);
    }

    private int positiveInt(String path, int fallback) {
        int value = plugin.getConfig().getInt(path, fallback); return value > 0 ? value : fallback;
    }

    private String discoverString(String... paths) {
        for (String path : paths) {
            String value = plugin.getConfig().getString(path, "");
            if (value != null && !value.isBlank()) return value.trim();
        }
        return "";
    }

    private String trimSlash(String value) { return value.endsWith("/") ? value.substring(0, value.length() - 1) : value; }

    private String serializeItems(List<ItemStack> items) {
        try (ByteArrayOutputStream bytes = new ByteArrayOutputStream(); BukkitObjectOutputStream out = new BukkitObjectOutputStream(bytes)) {
            out.writeInt(items.size()); for (ItemStack item : items) out.writeObject(item); out.flush();
            return Base64.getEncoder().encodeToString(bytes.toByteArray());
        } catch (IOException ex) { throw new IllegalStateException("Cannot serialize death items", ex); }
    }

    private List<ItemStack> deserializeItems(String encoded) {
        try (BukkitObjectInputStream in = new BukkitObjectInputStream(new ByteArrayInputStream(Base64.getDecoder().decode(encoded)))) {
            int count = in.readInt(); List<ItemStack> result = new ArrayList<>(count);
            for (int i = 0; i < count; i++) result.add((ItemStack) in.readObject()); return result;
        } catch (IOException | ClassNotFoundException ex) { throw new IllegalStateException("Cannot deserialize death items", ex); }
    }

    private void loadJournal() throws IOException {
        if (!Files.isDirectory(journalDir)) return;
        try (var stream = Files.list(journalDir)) {
            stream.filter(p -> p.getFileName().toString().endsWith(".properties")).forEach(path -> {
                try (InputStream in = Files.newInputStream(path)) {
                    Properties p = new Properties(); p.load(in); DeathRecord r = DeathRecord.from(p);
                    boolean oldTerminal = r.isTerminal()
                        && Duration.between(r.updatedAt, Instant.now()).toDays() > 7;
                    // Never prune an offline player's chest coordinates before they were shown.
                    // The physical chest may live forever, so losing this journal row would make it
                    // impossible to fulfil the promised notification on the next join.
                    boolean notificationOwed = (r.chestCreated && !r.chestNotified)
                        || (r.coordinatesGranted && !r.coordinatesNotified);
                    boolean canPrune = oldTerminal && !notificationOwed;
                    if (canPrune) {
                        Files.deleteIfExists(path);
                        plugin.debugJournal("pruned old terminal record path=" + path + " deathId=" + r.deathId);
                    } else {
                        deaths.put(r.deathId, r);
                        plugin.debugJournal("loaded deathId=" + r.deathId + " player=" + r.playerId
                            + " stage=" + r.stage + " chestCreated=" + r.chestCreated
                            + " coordinatesGranted=" + r.coordinatesGranted
                            + " coordinatesNotified=" + r.coordinatesNotified
                            + " chestNotified=" + r.chestNotified
                            + " offerClosed=" + r.offerClosed + " revision=" + r.revision);
                    }
                } catch (Exception ex) { plugin.getLogger().log(Level.SEVERE, "Cannot load " + path, ex); }
            });
        }
    }

    private boolean persistLocal(DeathRecord record) {
        synchronized (record) {
            record.updatedAt = Instant.now();
            record.revision = Math.max(0L, record.revision) + 1L;
            try {
                Files.createDirectories(journalDir);
                Path target = journalDir.resolve(record.deathId + ".properties");
                Path tmp = target.resolveSibling(target.getFileName() + ".tmp");
                try (OutputStream out = Files.newOutputStream(tmp)) {
                    record.toProperties().store(out, "TaskForge death recovery");
                }
                try {
                    Files.move(tmp, target, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
                } catch (IOException notAtomic) {
                    Files.move(tmp, target, StandardCopyOption.REPLACE_EXISTING);
                }
                plugin.debugJournal("persisted deathId=" + record.deathId + " stage=" + record.stage
                    + " revision=" + record.revision + " itemsResolved=" + record.itemsResolved
                    + " chestCreated=" + record.chestCreated + " coordinatesNotified=" + record.coordinatesNotified
                    + " chestNotified=" + record.chestNotified + " offerClosed=" + record.offerClosed
                    + " path=" + target);
                return true;
            } catch (IOException ex) {
                plugin.getLogger().log(Level.SEVERE, "Cannot persist death " + record.deathId, ex);
                return false;
            }
        }
    }

    private void saveQuietly(DeathRecord record) {
        boolean saved = persistLocal(record);
        if (saved && !apiBaseUrl.isBlank() && !stopped.get()) syncState(record);
    }

    private String recordJson(DeathRecord r) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("deathId", r.deathId.toString());
        m.put("playerUuid", r.playerId.toString());
        m.put("playerName", r.playerName);
        m.put("worldUuid", r.worldUuid.toString());
        m.put("worldKey", r.worldKey);
        m.put("worldName", r.worldName);
        m.put("x", r.x); m.put("y", r.y); m.put("z", r.z);
        m.put("yaw", r.yaw); m.put("pitch", r.pitch);
        m.put("itemsPayload", r.itemsBase64);
        m.put("createdAtUtc", r.createdAt == null ? null : r.createdAt.toString());
        m.put("offerExpiresAtUtc", r.offerExpiresAt == null ? null : r.offerExpiresAt.toString());
        m.put("rescueEndsAtUtc", r.rescueEndsAt == null ? null : r.rescueEndsAt.toString());
        m.put("stage", r.stage == null ? Stage.OFFER.name() : r.stage.name());
        m.put("action", blankToNull(r.action));
        m.put("purchaseRequestId", null);
        m.put("chargedAmount", r.chargedAmount);
        m.put("paymentStatus", r.compensated ? "compensated" : (r.paymentConfirmed ? "confirmed" : (r.compensationPending ? "pending" : "none")));
        m.put("paymentErrorCode", blankToNull(r.paymentErrorCode));
        m.put("dropsReleased", r.dropsReleased);
        m.put("chestSpotReserved", r.chestSpotReserved);
        m.put("chestCreated", r.chestCreated);
        m.put("itemsResolved", r.itemsResolved);
        m.put("rescuePending", r.rescuePending);
        m.put("rescueCompleted", r.rescueCompleted);
        m.put("previousGameMode", blankToNull(r.previousGameMode));
        m.put("backendUnavailable", r.backendUnavailable);
        m.put("compensationPending", r.compensationPending);
        m.put("compensated", r.compensated);
        m.put("chestX", r.chestSpotReserved ? r.chestX : null);
        m.put("chestY", r.chestSpotReserved ? r.chestY : null);
        m.put("chestZ", r.chestSpotReserved ? r.chestZ : null);
        m.put("chestSecondX", r.chestSpotReserved ? r.chestSecondX : null);
        m.put("chestSecondY", r.chestSpotReserved ? r.chestSecondY : null);
        m.put("chestSecondZ", r.chestSpotReserved ? r.chestSecondZ : null);
        m.put("finalX", r.rescueCompleted ? r.finalX : null);
        m.put("finalY", r.rescueCompleted ? r.finalY : null);
        m.put("finalZ", r.rescueCompleted ? r.finalZ : null);
        m.put("lastError", blankToNull(r.lastError));
        m.put("revision", r.revision);
        return json(m);
    }

    private String json(Map<String, ?> values) {
        return GSON.toJson(values);
    }

    private String blankToNull(String value) {
        return value == null || value.isBlank() ? null : value;
    }

    private enum BackendResult { AVAILABLE, UNAVAILABLE, UNLINKED }
    private enum PurchaseOutcome { CONFIRMED, DENIED, UNAVAILABLE, UNLINKED }

    private static final class PurchaseResult {
        final PurchaseOutcome outcome;
        final String reason;
        final int balance;
        final int cost;
        final int charged;
        private PurchaseResult(PurchaseOutcome outcome, String reason, int balance, int cost, int charged) {
            this.outcome = outcome;
            this.reason = reason == null ? "" : reason;
            this.balance = balance;
            this.cost = cost;
            this.charged = charged;
        }
        static PurchaseResult confirmed(int charged) { return new PurchaseResult(PurchaseOutcome.CONFIRMED, "", -1, -1, charged); }
        static PurchaseResult denied(String reason, int balance, int cost) { return new PurchaseResult(PurchaseOutcome.DENIED, reason, balance, cost, 0); }
        static PurchaseResult unavailable(String reason) { return new PurchaseResult(PurchaseOutcome.UNAVAILABLE, reason, -1, -1, 0); }
        static PurchaseResult unlinked(String reason) { return new PurchaseResult(PurchaseOutcome.UNLINKED, reason, -1, -1, 0); }
    }
    private enum Stage {
        WAITING_RESPAWN, OFFER, RESOLVING, WORLD_UNAVAILABLE, RESCUE_PENDING, RESCUE_ACTIVE,
        DROPS_RELEASED, CHEST_CREATED, FREE_CHEST_CREATED, COORDINATES_SENT, RESCUE_COMPLETED, CHEST_AND_RESCUE_COMPLETED;
    }

    private static final class ChunkCandidate {
        final int x, z;
        final double distance;
        ChunkCandidate(int x, int z, double distance) { this.x = x; this.z = z; this.distance = distance; }
    }

    private static final class ChestSpot {
        final Location first, second; final boolean eastWest;
        ChestSpot(Location first, Location second, boolean eastWest) { this.first = first; this.second = second; this.eastWest = eastWest; }
    }

    private static final class RescueSession {
        final DeathRecord record; final Location center; final Location fallback; final Instant endsAt; volatile boolean internalTeleport;
        RescueSession(DeathRecord record, Location center, Location fallback, Instant endsAt) {
            this.record = record; this.center = center; this.fallback = fallback; this.endsAt = endsAt;
        }
        long secondsRemaining() { return Math.max(0, Duration.between(Instant.now(), endsAt).toSeconds()); }
    }

    private static final class PendingVanillaDeathNotice {
        final UUID playerId;
        final String playerName;
        final TaskForgeLinkPlugin.LinkState state;
        final int dropStacks;
        final String worldKey;
        final double x;
        final double y;
        final double z;
        final Instant createdAt;
        final String source;
        boolean dispatchPending;

        PendingVanillaDeathNotice(UUID playerId, String playerName, TaskForgeLinkPlugin.LinkState state,
                                  int dropStacks, String worldKey, double x, double y, double z,
                                  Instant createdAt, String source) {
            this.playerId = playerId;
            this.playerName = playerName == null ? "unknown" : playerName;
            this.state = state == null ? TaskForgeLinkPlugin.LinkState.UNKNOWN : state;
            this.dropStacks = dropStacks;
            this.worldKey = worldKey == null ? "unknown" : worldKey;
            this.x = x;
            this.y = y;
            this.z = z;
            this.createdAt = createdAt == null ? Instant.now() : createdAt;
            this.source = source == null ? "unknown" : source;
        }
    }

    private static final class DeathRecord {
        UUID deathId, playerId, worldUuid, requestId;
        String playerName, worldKey, worldName, itemsBase64, action, previousGameMode;
        String paymentErrorCode, lastError, compensationAction;
        double x, y, z, finalX, finalY, finalZ; float yaw, pitch;
        int chestX, chestY, chestZ, chestSecondX, chestSecondY, chestSecondZ, chargedAmount, compensationAmount;
        Instant createdAt, offerExpiresAt, updatedAt, rescueEndsAt;
        transient volatile Instant retryAfter = Instant.EPOCH;
        transient volatile Instant compensationRetryAfter = Instant.EPOCH;
        transient volatile boolean chestSearchInFlight;
        transient volatile Instant lastOfferShownAt = Instant.EPOCH;
        transient final Set<String> actionsInFlight = ConcurrentHashMap.newKeySet();
        Stage stage; boolean paymentConfirmed, dropsReleased, chestSpotReserved, chestCreated, itemsResolved, rescuePending, rescueCompleted;
        boolean backendUnavailable, compensationPending, compensated, userNotified;
        boolean offerClosed, coordinatesGranted, coordinatesNotified, chestPurchased, chestNotified, returnPurchased;
        volatile boolean notificationDispatchPending;
        volatile boolean coordinatesNotificationDispatchPending;
        volatile boolean chestNotificationDispatchPending;
        long revision;
        boolean isTerminal() { return offerClosed && itemsResolved && !compensationPending && !rescuePending; }
        Properties toProperties() {
            Properties p = new Properties();
            put(p,"deathId",deathId); put(p,"playerId",playerId); put(p,"playerName",playerName); put(p,"worldUuid",worldUuid); put(p,"worldKey",worldKey); put(p,"worldName",worldName);
            put(p,"x",x); put(p,"y",y); put(p,"z",z); put(p,"yaw",yaw); put(p,"pitch",pitch); put(p,"items",itemsBase64);
            put(p,"createdAt",createdAt); put(p,"offerExpiresAt",offerExpiresAt); put(p,"updatedAt",updatedAt); put(p,"rescueEndsAt",rescueEndsAt);
            put(p,"stage",stage); put(p,"action",action); put(p,"requestId",requestId); put(p,"previousGameMode",previousGameMode);
            put(p,"chargedAmount",chargedAmount); put(p,"paymentConfirmed",paymentConfirmed); put(p,"dropsReleased",dropsReleased); put(p,"chestSpotReserved",chestSpotReserved); put(p,"chestCreated",chestCreated); put(p,"itemsResolved",itemsResolved);
            put(p,"rescuePending",rescuePending); put(p,"rescueCompleted",rescueCompleted); put(p,"backendUnavailable",backendUnavailable); put(p,"compensationPending",compensationPending); put(p,"compensated",compensated); put(p,"userNotified",userNotified);
            put(p,"offerClosed",offerClosed); put(p,"coordinatesGranted",coordinatesGranted); put(p,"coordinatesNotified",coordinatesNotified); put(p,"chestPurchased",chestPurchased); put(p,"chestNotified",chestNotified); put(p,"returnPurchased",returnPurchased);
            put(p,"compensationAction",compensationAction); put(p,"compensationAmount",compensationAmount);
            put(p,"chestX",chestX); put(p,"chestY",chestY); put(p,"chestZ",chestZ); put(p,"chestSecondX",chestSecondX); put(p,"chestSecondY",chestSecondY); put(p,"chestSecondZ",chestSecondZ);
            put(p,"finalX",finalX); put(p,"finalY",finalY); put(p,"finalZ",finalZ);
            put(p,"paymentErrorCode",paymentErrorCode); put(p,"lastError",lastError); put(p,"revision",revision); return p;
        }
        static DeathRecord from(Properties p) {
            DeathRecord r = new DeathRecord();
            r.deathId=UUID.fromString(p.getProperty("deathId")); r.playerId=UUID.fromString(p.getProperty("playerId")); r.playerName=p.getProperty("playerName","");
            r.worldUuid=UUID.fromString(p.getProperty("worldUuid")); r.worldKey=p.getProperty("worldKey",""); r.worldName=p.getProperty("worldName","");
            r.x=d(p,"x"); r.y=d(p,"y"); r.z=d(p,"z"); r.yaw=(float)d(p,"yaw"); r.pitch=(float)d(p,"pitch"); r.itemsBase64=p.getProperty("items","");
            r.createdAt=i(p,"createdAt",Instant.now()); r.offerExpiresAt=i(p,"offerExpiresAt",Instant.now()); r.updatedAt=i(p,"updatedAt",r.createdAt); r.rescueEndsAt=i(p,"rescueEndsAt",null);
            try { r.stage=Stage.valueOf(p.getProperty("stage","OFFER")); } catch(Exception e) { r.stage=Stage.OFFER; }
            r.action=p.getProperty("action",""); r.requestId=UUID.fromString(p.getProperty("requestId",UUID.randomUUID().toString())); r.previousGameMode=p.getProperty("previousGameMode","");
            r.chargedAmount=n(p,"chargedAmount"); r.paymentConfirmed=b(p,"paymentConfirmed"); r.dropsReleased=b(p,"dropsReleased"); r.chestSpotReserved=b(p,"chestSpotReserved"); r.chestCreated=b(p,"chestCreated"); r.itemsResolved=b(p,"itemsResolved");
            r.rescuePending=b(p,"rescuePending"); r.rescueCompleted=b(p,"rescueCompleted"); r.backendUnavailable=b(p,"backendUnavailable"); r.compensationPending=b(p,"compensationPending"); r.compensated=b(p,"compensated"); r.userNotified=b(p,"userNotified");
            boolean hasCoordinatesGranted = p.containsKey("coordinatesGranted");
            boolean hasCoordinatesNotified = p.containsKey("coordinatesNotified");
            boolean hasChestPurchased = p.containsKey("chestPurchased");
            boolean hasChestNotified = p.containsKey("chestNotified");
            boolean hasReturnPurchased = p.containsKey("returnPurchased");
            r.offerClosed=b(p,"offerClosed"); r.coordinatesGranted=b(p,"coordinatesGranted"); r.coordinatesNotified=b(p,"coordinatesNotified"); r.chestPurchased=b(p,"chestPurchased"); r.chestNotified=b(p,"chestNotified"); r.returnPurchased=b(p,"returnPurchased");
            r.compensationAction=p.getProperty("compensationAction",""); r.compensationAmount=n(p,"compensationAmount");
            // Legacy inference is only allowed when the field did not exist at all. New journal rows
            // deliberately persist false values per action. Treating an explicit false as "missing"
            // would let an earlier coordinates payment make a later chest/return look paid after restart.
            if (!hasCoordinatesGranted && !r.coordinatesGranted && ACTION_COORDINATES.equals(r.action) && (r.stage == Stage.COORDINATES_SENT || r.paymentConfirmed)) r.coordinatesGranted=true;
            if (!hasCoordinatesNotified && !r.coordinatesNotified && r.coordinatesGranted && r.userNotified) r.coordinatesNotified=true;
            if (!hasChestPurchased && !r.chestPurchased && r.paymentConfirmed && (ACTION_CHEST.equals(r.action) || ACTION_BOTH.equals(r.action))) r.chestPurchased=true;
            if (!hasChestNotified && !r.chestNotified && r.chestCreated && r.userNotified) r.chestNotified=true;
            if (!hasReturnPurchased && !r.returnPurchased && r.paymentConfirmed && (ACTION_RETURN.equals(r.action) || ACTION_BOTH.equals(r.action))) r.returnPurchased=true;
            if (!r.offerClosed && r.offerExpiresAt != null && Instant.now().isAfter(r.offerExpiresAt)) r.offerClosed=true;
            r.chestX=n(p,"chestX"); r.chestY=n(p,"chestY"); r.chestZ=n(p,"chestZ"); r.chestSecondX=n(p,"chestSecondX"); r.chestSecondY=n(p,"chestSecondY"); r.chestSecondZ=n(p,"chestSecondZ");
            r.finalX=d(p,"finalX"); r.finalY=d(p,"finalY"); r.finalZ=d(p,"finalZ");
            r.paymentErrorCode=p.getProperty("paymentErrorCode",""); r.lastError=p.getProperty("lastError",""); r.revision=l(p,"revision"); return r;
        }
        static void put(Properties p,String k,Object v){if(v!=null)p.setProperty(k,String.valueOf(v));}
        static int n(Properties p,String k){try{return Integer.parseInt(p.getProperty(k,"0"));}catch(Exception e){return 0;}}
        static long l(Properties p,String k){try{return Long.parseLong(p.getProperty(k,"0"));}catch(Exception e){return 0;}}
        static double d(Properties p,String k){try{return Double.parseDouble(p.getProperty(k,"0"));}catch(Exception e){return 0;}}
        static boolean b(Properties p,String k){return Boolean.parseBoolean(p.getProperty(k,"false"));}
        static Instant i(Properties p,String k,Instant f){try{String v=p.getProperty(k);return v==null||v.isBlank()?f:Instant.parse(v);}catch(Exception e){return f;}}
    }
}
