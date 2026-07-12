package com.taskforge.folia;

import com.google.gson.Gson;
import com.google.gson.JsonSyntaxException;
import com.sun.net.httpserver.Headers;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;
import com.sun.net.httpserver.HttpServer;
import org.bukkit.Bukkit;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.command.Command;
import org.bukkit.command.CommandSender;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.PlayerDeathEvent;
import org.bukkit.event.player.AsyncPlayerChatEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.event.player.PlayerRespawnEvent;
import org.bukkit.event.player.PlayerAdvancementDoneEvent;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.plugin.java.JavaPlugin;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.event.ClickEvent;
import net.kyori.adventure.text.event.HoverEvent;
import net.kyori.adventure.text.format.NamedTextColor;
import net.kyori.adventure.title.Title;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.util.Locale;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Optional;
import java.util.Set;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Folia/Paper plugin.
 *
 * Сейчас реализовано:
 *  - HTTP endpoint, куда тыкается TaskForge: POST {path}
 *  - Валидация X-TaskForge-Key
 *  - Идемпотентность по X-Request-Id
 *  - Доставка кода игроку в личку (если онлайн)
 */
public final class TaskForgeLinkPlugin extends JavaPlugin {

    private static final Gson GSON = new Gson();

    private HttpServer server;
    private ExecutorService serverExecutor;
    private final ConcurrentHashMap<String, Instant> seenRequestIds = new ConcurrentHashMap<>();
    private ScheduledExecutorService janitor;

    private volatile HttpClient httpClient;
    private final AtomicInteger consecutiveTaskForgeFailures = new AtomicInteger(0);
    private volatile Instant lastTaskForgeSuccessUtc = Instant.EPOCH;

    private ExecutorService tfExecutor;
    private String taskForgeBaseUrl;
    private String taskForgeKey;
    private int taskForgeTimeoutSeconds;
    private boolean chatEnabled;
    private int chatPollIntervalSeconds;
    private boolean chatPollOnlyWhenPlayersOnline;
    private boolean chatForwardJoinQuit;
    private boolean chatForwardPlayerMessages;
    private boolean chatForwardAdvancements;
    private boolean chatIncludeRecipeAdvancements;
    private boolean chatIncludeRootAdvancements;
    private String chatSitePrefix;
    private String chatMinecraftPrefix;
    private volatile Instant chatCursorUtc = Instant.EPOCH;
    private boolean deathTeleportEnabled;
    private int deathTeleportCost;
    private int deathTeleportOfferTtlSeconds;
    private int deathTeleportSafeRadius;
    private int deathTeleportMessageDelayTicks;
    private int deathTeleportSpectatorSeconds;
    private int deathTeleportPreviewMaxDistance;
    private int deathTeleportFireResistanceSeconds;
    private int deathTeleportResistanceSeconds;
    private int deathTeleportRegenerationSeconds;
    private int deathTeleportSlowFallingSeconds;
    private NamespacedKey rescueOriginalModeKey;
    private NamespacedKey rescueActiveKey;
    private final ConcurrentHashMap<java.util.UUID, DeathTeleportOffer> deathOffers = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<java.util.UUID, DeathRescueSession> rescueSessions = new ConcurrentHashMap<>();

    // Игроки-исключения: без запросов в API и игровых сообщений TaskForge.
    private final Set<String> exemptNicksLower = ConcurrentHashMap.newKeySet();
    private final Set<String> exemptUuidsLower = ConcurrentHashMap.newKeySet();

    private void migrateConfigKeys() {
        boolean changed = false;

        // security.taskforgekey -> security.taskforgeKey
        String oldTfKey = getConfig().getString("security.taskforgekey");
        String newTfKey = getConfig().getString("security.taskforgeKey");
        if (isBlank(newTfKey) && !isBlank(oldTfKey)) {
            getConfig().set("security.taskforgeKey", oldTfKey);
            changed = true;
        }

        // taskforge.apibaseUrl -> taskforge.apiBaseUrl
        String oldBase = getConfig().getString("taskforge.apibaseUrl");
        String newBase = getConfig().getString("taskforge.apiBaseUrl");
        if (isBlank(newBase) && !isBlank(oldBase)) {
            getConfig().set("taskforge.apiBaseUrl", oldBase);
            changed = true;
        }

        // taskforge.pluginkey -> taskforge.pluginKey
        String oldPluginKey = getConfig().getString("taskforge.pluginkey");
        String newPluginKey = getConfig().getString("taskforge.pluginKey");
        if (isBlank(newPluginKey) && !isBlank(oldPluginKey)) {
            getConfig().set("taskforge.pluginKey", oldPluginKey);
            changed = true;
        }

        if (changed) {
            saveConfig();
            getLogger().info("Config keys were migrated to canonical names (camelCase). Re-check plugins/TaskForgeLink/config.yml if you edited it manually.");
        }
    }

    private static String firstNonBlank(String... values) {
        if (values == null) return "";
        for (String v : values) {
            if (!isBlank(v)) return v.trim();
        }
        return "";
    }

    private static boolean isBlank(String s) {
        return s == null || s.trim().isEmpty();
    }

    private HttpClient buildHttpClient() {
        return HttpClient.newBuilder()
                .version(HttpClient.Version.HTTP_1_1)
                .connectTimeout(Duration.ofSeconds(Math.max(2, taskForgeTimeoutSeconds > 0 ? taskForgeTimeoutSeconds : 5)))
                .build();
    }

    private void markTaskForgeSuccess() {
        consecutiveTaskForgeFailures.set(0);
        lastTaskForgeSuccessUtc = Instant.now();
    }

    private void markTaskForgeFailure(String scope, Throwable ex) {
        int failures = consecutiveTaskForgeFailures.incrementAndGet();
        String msg = ex == null ? "unknown" : ex.getClass().getSimpleName() + ": " + ex.getMessage();
        getLogger().warning("TaskForge " + scope + " call failed (" + failures + " in a row): " + msg);
        if (failures >= 3) {
            getLogger().warning("TaskForge connectivity looks stale, rebuilding HTTP client.");
            httpClient = buildHttpClient();
            consecutiveTaskForgeFailures.set(0);
        }
    }

    private HttpRequest.Builder newTaskForgeRequest(String url) {
        return HttpRequest.newBuilder()
                .uri(URI.create(url))
                .timeout(Duration.ofSeconds(taskForgeTimeoutSeconds))
                .header("X-Minecraft-Key", taskForgeKey);
    }

    @Override
    public void onEnable() {
        saveDefaultConfig();

        // Backward/typo compatibility:
        // Some configs may contain wrong-cased keys like `taskforgekey`.
        // We read both and auto-migrate to the canonical camelCase keys.
        migrateConfigKeys();

        String host = getConfig().getString("http.host", "0.0.0.0");
        int port = getConfig().getInt("http.port", 25566);
        String path = getConfig().getString("http.path", "/taskforge/link/send");

        String key = firstNonBlank(
                getConfig().getString("security.taskforgeKey", ""),
                getConfig().getString("security.taskforgekey", "")
        );
        if (key == null) key = "";

        List<String> allowedIps = getConfig().getStringList("security.allowedIps");

        taskForgeBaseUrl = firstNonBlank(
                Optional.ofNullable(getConfig().getString("taskforge.apiBaseUrl")).orElse("").trim(),
                Optional.ofNullable(getConfig().getString("taskforge.apibaseUrl")).orElse("").trim()
        );

        taskForgeKey = firstNonBlank(
                Optional.ofNullable(getConfig().getString("taskforge.pluginKey")).orElse("").trim(),
                Optional.ofNullable(getConfig().getString("taskforge.pluginkey")).orElse("").trim()
        );
        taskForgeTimeoutSeconds = Math.max(1, getConfig().getInt("taskforge.timeoutSeconds", 4));
        chatEnabled = getConfig().getBoolean("chat.enabled", true);
        chatPollIntervalSeconds = Math.max(5, getConfig().getInt("chat.pollIntervalSeconds", 12));
        chatPollOnlyWhenPlayersOnline = getConfig().getBoolean("chat.pollOnlyWhenPlayersOnline", true);
        chatForwardJoinQuit = getConfig().getBoolean("chat.forwardJoinQuit", true);
        chatForwardPlayerMessages = getConfig().getBoolean("chat.forwardPlayerMessages", true);
        chatForwardAdvancements = getConfig().getBoolean("chat.forwardAdvancements", true);
        chatIncludeRecipeAdvancements = getConfig().getBoolean("chat.includeRecipeAdvancements", false);
        chatIncludeRootAdvancements = getConfig().getBoolean("chat.includeRootAdvancements", false);
        chatSitePrefix = getConfig().getString("chat.sitePrefix", "§d[TaskForge]§r ");
        chatMinecraftPrefix = getConfig().getString("chat.minecraftPrefix", "[MC] ");
        chatCursorUtc = Instant.now();
        deathTeleportEnabled = getConfig().getBoolean("deathTeleport.enabled", true);
        deathTeleportCost = Math.max(1, getConfig().getInt("deathTeleport.cost", 100));
        deathTeleportOfferTtlSeconds = Math.max(10, getConfig().getInt("deathTeleport.offerTtlSeconds", 300));
        deathTeleportMessageDelayTicks = Math.max(1, getConfig().getInt("deathTeleport.messageDelayTicks", 20));
        int legacySafeRadius = Math.max(0, getConfig().getInt("deathTeleport.safeRadius", 8));
        deathTeleportSafeRadius = Math.max(0, getConfig().getInt("deathTeleport.recovery.safeSearchRadius", legacySafeRadius));
        deathTeleportSpectatorSeconds = Math.max(3, getConfig().getInt("deathTeleport.recovery.spectatorSeconds", 10));
        deathTeleportPreviewMaxDistance = Math.max(8, getConfig().getInt("deathTeleport.recovery.maxDistance", 24));
        deathTeleportFireResistanceSeconds = Math.max(0, getConfig().getInt("deathTeleport.recovery.fireResistanceSeconds", 60));
        deathTeleportResistanceSeconds = Math.max(0, getConfig().getInt("deathTeleport.recovery.resistanceSeconds", 10));
        deathTeleportRegenerationSeconds = Math.max(0, getConfig().getInt("deathTeleport.recovery.regenerationSeconds", 10));
        deathTeleportSlowFallingSeconds = Math.max(0, getConfig().getInt("deathTeleport.recovery.slowFallingSeconds", 10));
        rescueOriginalModeKey = new NamespacedKey(this, "rescue_original_mode");
        rescueActiveKey = new NamespacedKey(this, "rescue_active");

        // exemptions
        exemptNicksLower.clear();
        exemptUuidsLower.clear();
        for (String n : getConfig().getStringList("exemptions.nicks")) {
            if (n != null && !n.trim().isEmpty()) exemptNicksLower.add(n.trim().toLowerCase(Locale.ROOT));
        }
        for (String u : getConfig().getStringList("exemptions.uuids")) {
            if (u != null && !u.trim().isEmpty()) exemptUuidsLower.add(u.trim().toLowerCase(Locale.ROOT));
        }

        httpClient = buildHttpClient();

        try {
            InetAddress addr = InetAddress.getByName(host);
            server = HttpServer.create(new InetSocketAddress(addr, port), 0);
            server.createContext(path, new SendCodeHandler(this, key, allowedIps));
            // Простая проверка доступности (без ключей)
            server.createContext("/health", ex -> {
                int onlinePlayers = Bukkit.getOnlinePlayers().size();
                String resp = "{\"ok\":true,\"onlinePlayers\":" + onlinePlayers + "}";
                ex.getResponseHeaders().add("Content-Type", "application/json");
                ex.getResponseHeaders().add("Connection", "close");
                ex.sendResponseHeaders(200, resp.getBytes(StandardCharsets.UTF_8).length);
                try (OutputStream os = ex.getResponseBody()) {
                    os.write(resp.getBytes(StandardCharsets.UTF_8));
                }
            });
            serverExecutor = Executors.newFixedThreadPool(8, r -> {
                Thread t = new Thread(r, "taskforge-link-http-server");
                t.setDaemon(true);
                return t;
            });
            server.setExecutor(serverExecutor);
            server.start();

            getLogger().info("TaskForgeLink HTTP server started on " + host + ":" + port + " path=" + path);
        } catch (Exception ex) {
            getLogger().severe("Failed to start HTTP server: " + ex.getMessage());
            // Если не подняли HTTP — лучше выключить плагин, чтобы не было иллюзий что всё работает.
            Bukkit.getPluginManager().disablePlugin(this);
            return;
        }

        // Listener: plugin -> TaskForge
        tfExecutor = Executors.newFixedThreadPool(2, r -> {
            Thread t = new Thread(r, "taskforge-link-http");
            t.setDaemon(true);
            return t;
        });

        Bukkit.getPluginManager().registerEvents(new TfListener(this), this);
        for (Player player : Bukkit.getOnlinePlayers()) {
            player.getScheduler().run(this, task -> recoverInterruptedRescue(player), null);
        }

        // Очистка кеша requestId, чтобы не рос бесконечно
        janitor = Executors.newSingleThreadScheduledExecutor(r -> {
            Thread t = new Thread(r, "taskforge-link-janitor");
            t.setDaemon(true);
            return t;
        });
        janitor.scheduleAtFixedRate(this::cleanupRequestCache, 5, 5, TimeUnit.MINUTES);
        if (chatEnabled && canCallTaskForge()) {
            janitor.scheduleAtFixedRate(this::pollSiteChatSafe, chatPollIntervalSeconds, chatPollIntervalSeconds, TimeUnit.SECONDS);
        }
    }

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        if (!"tfback".equalsIgnoreCase(command.getName())) return false;
        if (!(sender instanceof Player p)) {
            sender.sendMessage("Only players can use this command.");
            return true;
        }
        if (args.length >= 2 && "confirm".equalsIgnoreCase(args[0])) {
            handleDeathTeleportCommand(p, args[1]);
            return true;
        }
        if (args.length >= 1 && ("cancel".equalsIgnoreCase(args[0]) || "abort".equalsIgnoreCase(args[0]))) {
            DeathRescueSession active = rescueSessions.get(p.getUniqueId());
            if (active != null) {
                if (active.purchasePending || active.purchaseCompleted) {
                    p.sendMessage("§eTaskForge: §7операция уже подтверждается. Подожди завершения.");
                    return true;
                }
                cancelRescueWithoutCharge(p, active, "§eTaskForge: §7возврат отменён. Рейтинг не списан.");
                return true;
            }
            if (args.length >= 2) {
                cancelDeathTeleportOffer(p, args[1]);
                return true;
            }
        }
        DeathTeleportOffer offer = deathOffers.get(p.getUniqueId());
        if (offer == null || offer.expiresAt.isBefore(Instant.now())) {
            p.sendMessage("§eTaskForge: §7нет актуального места смерти для возврата.");
            return true;
        }
        showDeathTeleportOffer(p);
        return true;
    }

    @Override
    public void onDisable() {
        if (server != null) {
            server.stop(0);
            server = null;
        }
        if (serverExecutor != null) {
            serverExecutor.shutdownNow();
            serverExecutor = null;
        }
        if (janitor != null) {
            janitor.shutdownNow();
            janitor = null;
        }
        if (tfExecutor != null) {
            tfExecutor.shutdownNow();
            tfExecutor = null;
        }
        seenRequestIds.clear();
        deathOffers.clear();
        rescueSessions.clear();
    }

    private void cleanupRequestCache() {
        Instant now = Instant.now();
        for (Map.Entry<String, Instant> e : seenRequestIds.entrySet()) {
            if (Duration.between(e.getValue(), now).toMinutes() >= 30) {
                seenRequestIds.remove(e.getKey());
            }
        }
        for (Map.Entry<java.util.UUID, DeathTeleportOffer> e : deathOffers.entrySet()) {
            DeathTeleportOffer offer = e.getValue();
            boolean activeRescue = rescueSessions.containsKey(e.getKey());
            if (offer == null || offer.expiresAt.isBefore(now) || (offer.used && !activeRescue)) {
                deathOffers.remove(e.getKey(), offer);
            }
        }
    }

    boolean markRequestIdOnce(String requestId) {
        if (requestId == null || requestId.isBlank()) return false;
        return seenRequestIds.putIfAbsent(requestId, Instant.now()) == null;
    }

    boolean canCallTaskForge() {
        return !taskForgeBaseUrl.isBlank() && !taskForgeKey.isBlank();
    }

    CompletableFuture<PlayerStatusResponse> notifyJoinAsync(Player p) {
        if (!canCallTaskForge()) {
            return CompletableFuture.completedFuture(null);
        }

        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/events/join";
        String body = GSON.toJson(new JoinEventRequest(p.getName(), p.getUniqueId().toString()));

        HttpRequest req = newTaskForgeRequest(url)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8))
                .build();

        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .thenApply(resp -> { if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess(); return parseStatusResponse(resp); })
                .exceptionally(ex -> {
                    markTaskForgeFailure("join", ex);
                    return null;
                });
    }

    CompletableFuture<PlayerStatusResponse> getStatusAsync(Player p) {
        if (!canCallTaskForge()) {
            return CompletableFuture.completedFuture(null);
        }
        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/player-status?uuid=" + p.getUniqueId();

        HttpRequest req = newTaskForgeRequest(url)
                .GET()
                .build();

        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .thenApply(resp -> { if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess(); return parseStatusResponse(resp); })
                .exceptionally(ex -> {
                    markTaskForgeFailure("status", ex);
                    return null;
                });
    }

    private boolean shouldPollSiteChatNow() {
        if (!chatEnabled || !canCallTaskForge()) return false;
        if (!chatPollOnlyWhenPlayersOnline) return true;
        return !Bukkit.getOnlinePlayers().isEmpty();
    }

    private boolean shouldForwardAdvancementKey(String key) {
        if (!chatForwardAdvancements) return false;
        String lower = (key == null ? "" : key.trim().toLowerCase(Locale.ROOT));
        if (lower.isEmpty()) return false;
        if (!chatIncludeRecipeAdvancements && lower.contains("recipes/")) return false;
        if (!chatIncludeRootAdvancements && lower.endsWith("/root")) return false;
        return true;
    }

    void sendChat(Player p, String message) {
        if (p == null || message == null) return;
        p.getScheduler().run(this, task -> {
            if (!p.isOnline()) return;
            p.sendMessage(message);
        }, null);
    }

    boolean isExempt(Player p) {
        if (p == null) return false;
        String nick = p.getName() == null ? "" : p.getName().trim().toLowerCase(Locale.ROOT);
        if (!nick.isEmpty() && exemptNicksLower.contains(nick)) return true;

        String uuid = p.getUniqueId() == null ? "" : p.getUniqueId().toString().trim().toLowerCase(Locale.ROOT);
        return !uuid.isEmpty() && exemptUuidsLower.contains(uuid);
    }

    private static String normalizeBase(String base) {
        if (base.endsWith("/")) return base.substring(0, base.length() - 1);
        return base;
    }

    private PlayerStatusResponse parseStatusResponse(HttpResponse<String> resp) {
        if (resp == null) return null;
        String raw = resp.body() == null ? "" : resp.body();
        String preview = raw.length() > 300 ? raw.substring(0, 300) + "..." : raw;

        if (resp.statusCode() < 200 || resp.statusCode() >= 300) {
            getLogger().warning("[MC->TF] call failed: status=" + resp.statusCode() + " body=" + preview);
            return null;
        }
        try {
            return GSON.fromJson(raw, PlayerStatusResponse.class);
        } catch (Exception e) {
            getLogger().warning("[MC->TF] parse error: " + e.getMessage() + " body=" + preview);
            return null;
        }
    }

    CompletableFuture<DeathTeleportQuoteResponse> quoteDeathTeleportAsync(Player p, DeathTeleportOffer offer) {
        if (!canCallTaskForge() || p == null || offer == null) {
            return CompletableFuture.completedFuture(null);
        }
        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/death-teleport/quote";
        String body = GSON.toJson(new DeathTeleportQuoteRequest(p.getName(), p.getUniqueId().toString(), offer.deathId));
        HttpRequest req = newTaskForgeRequest(url)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8))
                .build();
        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 2L, TimeUnit.SECONDS)
                .thenApply(resp -> {
                    if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
                    if (resp == null || resp.statusCode() < 200 || resp.statusCode() >= 300) return null;
                    try { return GSON.fromJson(resp.body(), DeathTeleportQuoteResponse.class); }
                    catch (Exception ex) { return null; }
                })
                .exceptionally(ex -> {
                    markTaskForgeFailure("death-teleport-quote", ex);
                    return null;
                });
    }

    CompletableFuture<DeathTeleportPurchaseResponse> purchaseDeathTeleportAsync(Player p, DeathTeleportOffer offer) {
        if (!canCallTaskForge() || p == null || offer == null) {
            return CompletableFuture.completedFuture(null);
        }
        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/death-teleport/purchase";
        String body = GSON.toJson(new DeathTeleportPurchaseRequest(p.getName(), p.getUniqueId().toString(), offer.deathId, offer.requestId));
        HttpRequest req = newTaskForgeRequest(url)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8))
                .build();
        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 2L, TimeUnit.SECONDS)
                .thenApply(resp -> {
                    if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
                    if (resp == null || resp.statusCode() < 200 || resp.statusCode() >= 300) return null;
                    try { return GSON.fromJson(resp.body(), DeathTeleportPurchaseResponse.class); }
                    catch (Exception ex) { return null; }
                })
                .exceptionally(ex -> {
                    markTaskForgeFailure("death-teleport-purchase", ex);
                    return null;
                });
    }

    void rememberDeathLocation(Player p, Location location) {
        if (!deathTeleportEnabled || p == null || location == null || location.getWorld() == null || isExempt(p)) return;
        String deathId = java.util.UUID.randomUUID().toString().replace("-", "");
        DeathTeleportOffer offer = new DeathTeleportOffer(
                deathId,
                p.getUniqueId().toString() + ":" + deathId,
                location.getWorld().getName(),
                location.getX(),
                location.getY(),
                location.getZ(),
                location.getYaw(),
                location.getPitch(),
                Instant.now().plusSeconds(deathTeleportOfferTtlSeconds),
                deathTeleportCost
        );
        deathOffers.put(p.getUniqueId(), offer);
    }

    void showDeathTeleportOffer(Player p) {
        if (!deathTeleportEnabled || p == null || isExempt(p) || !canCallTaskForge()) return;
        DeathTeleportOffer offer = deathOffers.get(p.getUniqueId());
        if (offer == null || offer.used || offer.expiresAt.isBefore(Instant.now())) return;
        quoteDeathTeleportAsync(p, offer).thenAcceptAsync(q -> {
            if (q == null) {
                sendChat(p, "§eTaskForge: §7место смерти сохранено, но баланс сейчас не удалось проверить.");
                return;
            }
            p.getScheduler().run(this, task -> {
                if (!p.isOnline()) return;
                if (!q.linked) {
                    p.sendMessage("§eTaskForge: §7привяжи Minecraft к аккаунту, чтобы возвращаться на место смерти за рейтинг.");
                    return;
                }
                int cost = q.cost > 0 ? q.cost : offer.cost;
                p.sendMessage("§6☠ §fТы умер. Minecraft-баланс: §e" + q.balance + "§f. Возврат стоит §e" + cost + "§f.");
                if (!q.allowed) {
                    p.sendMessage("§eTaskForge: §cне хватает рейтинга для возврата на место смерти.");
                    return;
                }
                Component returnButton = Component.text("[Вернуться за " + cost + "]", NamedTextColor.GREEN)
                        .clickEvent(ClickEvent.runCommand("/tfback confirm " + offer.deathId))
                        .hoverEvent(HoverEvent.showText(Component.text("10 секунд в режиме наблюдателя, затем безопасное возвращение", NamedTextColor.YELLOW)));
                Component cancelButton = Component.text("[Отказаться]", NamedTextColor.RED)
                        .clickEvent(ClickEvent.runCommand("/tfback cancel " + offer.deathId))
                        .hoverEvent(HoverEvent.showText(Component.text("Удалить предложение возврата", NamedTextColor.GRAY)));
                Component line = Component.text("TaskForge: ", NamedTextColor.GOLD)
                        .append(returnButton)
                        .append(Component.space())
                        .append(cancelButton)
                        .append(Component.text("  Действует " + deathTeleportOfferTtlSeconds + " сек.", NamedTextColor.GRAY));
                p.sendMessage(line);
            }, null);
        }, tfExecutor);
    }

    private Location rawDeathLocation(DeathTeleportOffer offer) {
        if (offer == null) return null;
        World world = Bukkit.getWorld(offer.worldName);
        if (world == null) return null;
        double y = Math.max(world.getMinHeight() + 1.0, Math.min(offer.y, world.getMaxHeight() - 2.0));
        return new Location(world, offer.x, y, offer.z, offer.yaw, offer.pitch);
    }

    private Location centered(Location location) {
        return new Location(location.getWorld(), location.getBlockX() + 0.5, location.getBlockY(), location.getBlockZ() + 0.5, location.getYaw(), location.getPitch());
    }

    private Location findSafeLocationNear(Location origin, int radius) {
        if (origin == null || origin.getWorld() == null) return null;
        if (isSafeLocation(origin)) return centered(origin);
        World world = origin.getWorld();
        int baseX = origin.getBlockX();
        int baseY = origin.getBlockY();
        int baseZ = origin.getBlockZ();
        for (int r = 1; r <= radius; r++) {
            for (int dx = -r; dx <= r; dx++) {
                for (int dz = -r; dz <= r; dz++) {
                    if (Math.abs(dx) != r && Math.abs(dz) != r) continue;
                    for (int dy = -4; dy <= 6; dy++) {
                        Location candidate = new Location(world, baseX + dx + 0.5, baseY + dy, baseZ + dz + 0.5, origin.getYaw(), origin.getPitch());
                        if (isSafeLocation(candidate)) return candidate;
                    }
                }
            }
        }
        return null;
    }

    private boolean isSafeLocation(Location location) {
        if (location == null || location.getWorld() == null) return false;
        World world = location.getWorld();
        int y = location.getBlockY();
        if (y <= world.getMinHeight() || y >= world.getMaxHeight() - 2) return false;
        Block below = world.getBlockAt(location.getBlockX(), y - 1, location.getBlockZ());
        Block feet = world.getBlockAt(location.getBlockX(), y, location.getBlockZ());
        Block head = world.getBlockAt(location.getBlockX(), y + 1, location.getBlockZ());
        return below.getType().isSolid()
                && !isHazardous(below.getType())
                && feet.isPassable()
                && !feet.isLiquid()
                && !isHazardous(feet.getType())
                && head.isPassable()
                && !head.isLiquid()
                && !isHazardous(head.getType());
    }

    private boolean isHazardous(Material material) {
        if (material == null) return true;
        return switch (material) {
            case LAVA, FIRE, SOUL_FIRE, MAGMA_BLOCK, CAMPFIRE, SOUL_CAMPFIRE,
                    CACTUS, SWEET_BERRY_BUSH, POWDER_SNOW, WITHER_ROSE -> true;
            default -> false;
        };
    }

    private void handleDeathTeleportCommand(Player p, String deathId) {
        if (!deathTeleportEnabled) {
            p.sendMessage("§eTaskForge: §7возврат на место смерти отключён.");
            return;
        }
        DeathTeleportOffer offer = deathOffers.get(p.getUniqueId());
        if (offer == null || !offer.deathId.equalsIgnoreCase(deathId)) {
            p.sendMessage("§eTaskForge: §cактуального предложения возврата нет.");
            return;
        }
        if (offer.used || offer.expiresAt.isBefore(Instant.now())) {
            deathOffers.remove(p.getUniqueId(), offer);
            p.sendMessage("§eTaskForge: §cпредложение возврата устарело.");
            return;
        }
        if (rescueSessions.containsKey(p.getUniqueId())) {
            p.sendMessage("§eTaskForge: §7возврат уже выполняется.");
            return;
        }
        Location target = rawDeathLocation(offer);
        if (target == null) {
            p.sendMessage("§eTaskForge: §cмир точки смерти сейчас недоступен. Рейтинг не списан.");
            return;
        }
        quoteDeathTeleportAsync(p, offer).thenAcceptAsync(q -> p.getScheduler().run(this, task -> {
            if (!p.isOnline()) return;
            if (q == null) {
                p.sendMessage("§eTaskForge: §cне удалось проверить баланс. Попробуй позже.");
                return;
            }
            if (!q.linked) {
                p.sendMessage("§eTaskForge: §cаккаунт Minecraft не привязан к TaskForge.");
                return;
            }
            if (!q.allowed) {
                p.sendMessage("§eTaskForge: §cне хватает рейтинга. Баланс: " + q.balance + ", нужно " + q.cost + ".");
                return;
            }
            startDeathRescue(p, offer, target);
        }, null), tfExecutor);
    }

    private void cancelDeathTeleportOffer(Player p, String deathId) {
        DeathTeleportOffer offer = deathOffers.get(p.getUniqueId());
        if (offer == null || !offer.deathId.equalsIgnoreCase(deathId)) {
            p.sendMessage("§eTaskForge: §7актуального предложения возврата нет.");
            return;
        }
        if (rescueSessions.containsKey(p.getUniqueId())) {
            p.sendMessage("§eTaskForge: §7возврат уже начался.");
            return;
        }
        deathOffers.remove(p.getUniqueId(), offer);
        p.sendMessage("§eTaskForge: §7возврат на место смерти отменён.");
    }

    private void startDeathRescue(Player p, DeathTeleportOffer offer, Location target) {
        if (offer.used || rescueSessions.containsKey(p.getUniqueId())) return;
        offer.used = true;
        GameMode originalMode = p.getGameMode();
        Location returnLocation = p.getLocation().clone();
        DeathRescueSession session = new DeathRescueSession(
                offer,
                originalMode,
                returnLocation,
                target.clone(),
                deathTeleportSpectatorSeconds
        );
        rescueSessions.put(p.getUniqueId(), session);
        p.getPersistentDataContainer().set(rescueActiveKey, PersistentDataType.BYTE, (byte) 1);
        p.getPersistentDataContainer().set(rescueOriginalModeKey, PersistentDataType.STRING, originalMode.name());
        p.setGameMode(GameMode.SPECTATOR);
        p.teleportAsync(target).thenAccept(ok -> p.getScheduler().run(this, task -> {
            if (!p.isOnline()) return;
            if (!ok) {
                cancelRescueWithoutCharge(p, session, "§eTaskForge: §cне удалось открыть точку смерти. Рейтинг не списан.");
                return;
            }
            p.sendMessage("§eTaskForge: §fу тебя §e" + deathTeleportSpectatorSeconds + " секунд§f, чтобы выбрать безопасное место рядом с точкой смерти.");
            Component cancelButton = Component.text("[Отменить без списания]", NamedTextColor.RED)
                    .clickEvent(ClickEvent.runCommand("/tfback cancel"))
                    .hoverEvent(HoverEvent.showText(Component.text("Вернуться на точку респавна", NamedTextColor.GRAY)));
            p.sendMessage(Component.text("TaskForge: ", NamedTextColor.GOLD).append(cancelButton));
            tickDeathRescue(p, session);
        }, null));
    }

    private void tickDeathRescue(Player p, DeathRescueSession session) {
        if (p == null || session == null || !p.isOnline()) return;
        if (rescueSessions.get(p.getUniqueId()) != session) return;
        Location current = p.getLocation();
        if (current.getWorld() == null
                || session.anchor.getWorld() == null
                || !current.getWorld().getUID().equals(session.anchor.getWorld().getUID())
                || current.distanceSquared(session.anchor) > (double) deathTeleportPreviewMaxDistance * deathTeleportPreviewMaxDistance) {
            p.teleportAsync(session.anchor);
            p.sendActionBar(Component.text("Не отходи дальше " + deathTeleportPreviewMaxDistance + " блоков от точки смерти", NamedTextColor.RED));
        }
        if (session.secondsLeft <= 0) {
            finishDeathRescue(p, session);
            return;
        }
        Component title = Component.text(Integer.toString(session.secondsLeft), session.secondsLeft <= 3 ? NamedTextColor.RED : NamedTextColor.GOLD);
        Component subtitle = Component.text("Безопасная точка • радиус " + deathTeleportPreviewMaxDistance + " блоков", NamedTextColor.YELLOW);
        p.showTitle(Title.title(
                title,
                subtitle,
                Title.Times.times(Duration.ZERO, Duration.ofMillis(900), Duration.ofMillis(100))
        ));
        p.sendActionBar(Component.text("Режим наблюдателя • списание произойдёт только после безопасного возвращения", NamedTextColor.AQUA));
        session.secondsLeft--;
        p.getScheduler().runDelayed(this, task -> tickDeathRescue(p, session), null, 20L);
    }

    private void finishDeathRescue(Player p, DeathRescueSession session) {
        if (rescueSessions.get(p.getUniqueId()) != session) return;
        Location chosen = p.getLocation();
        if (chosen.getWorld() == null
                || session.anchor.getWorld() == null
                || !chosen.getWorld().getUID().equals(session.anchor.getWorld().getUID())
                || chosen.distanceSquared(session.anchor) > (double) deathTeleportPreviewMaxDistance * deathTeleportPreviewMaxDistance) {
            cancelRescueWithoutCharge(p, session, "§eTaskForge: §cвыбранная точка слишком далеко от места смерти. Рейтинг не списан.");
            return;
        }
        Location safeTarget = findSafeLocationNear(chosen, deathTeleportSafeRadius);
        if (safeTarget == null) {
            cancelRescueWithoutCharge(p, session, "§eTaskForge: §cрядом с выбранной точкой нет безопасного места. Рейтинг не списан.");
            return;
        }
        session.finalLocation = safeTarget;
        p.showTitle(Title.title(
                Component.text("Точка выбрана", NamedTextColor.GREEN),
                Component.text("Фиксируем позицию перед списанием", NamedTextColor.GRAY),
                Title.Times.times(Duration.ofMillis(100), Duration.ofMillis(1200), Duration.ofMillis(200))
        ));
        p.teleportAsync(safeTarget).thenAccept(ok -> p.getScheduler().run(this, task -> {
            if (!p.isOnline() || rescueSessions.get(p.getUniqueId()) != session) return;
            if (!ok) {
                cancelRescueWithoutCharge(p, session, "§eTaskForge: §cне удалось зафиксировать безопасную точку. Рейтинг не списан.");
                return;
            }
            session.locked = true;
            session.purchasePending = true;
            p.showTitle(Title.title(
                    Component.text("Подтверждение", NamedTextColor.GOLD),
                    Component.text("Проверяем баланс TaskForge", NamedTextColor.GRAY),
                    Title.Times.times(Duration.ofMillis(100), Duration.ofSeconds(2), Duration.ofMillis(200))
            ));
            purchaseDeathTeleportAsync(p, session.offer).thenAcceptAsync(result -> {
                session.purchasePending = false;
                if (result != null && result.success) {
                    session.purchaseCompleted = true;
                    session.purchaseResult = result;
                }
                if (!p.isOnline()) return;
                p.getScheduler().run(this, next -> {
                    if (rescueSessions.get(p.getUniqueId()) != session) return;
                    if (result == null) {
                        cancelRescueWithoutCharge(p, session, "§eTaskForge: §cне удалось списать рейтинг. Ты возвращён на точку респавна.");
                        return;
                    }
                    if (!result.success) {
                        String message;
                        if ("not-enough-rating".equalsIgnoreCase(result.reason)) {
                            message = "§eTaskForge: §cбаланс изменился: " + result.balance + ", нужно " + result.cost + ". Рейтинг не списан.";
                        } else if ("not-linked".equalsIgnoreCase(result.reason)) {
                            message = "§eTaskForge: §cаккаунт Minecraft больше не привязан. Рейтинг не списан.";
                        } else {
                            message = "§eTaskForge: §cвозврат сейчас недоступен. Рейтинг не списан.";
                        }
                        cancelRescueWithoutCharge(p, session, message);
                        return;
                    }
                    completePaidRescue(p, session, result);
                }, null);
            }, tfExecutor);
        }, null));
    }

    private void completePaidRescue(Player p, DeathRescueSession session, DeathTeleportPurchaseResponse result) {
        restoreRescueState(p, session);
        applyRecoveryProtection(p);
        rescueSessions.remove(p.getUniqueId(), session);
        deathOffers.remove(p.getUniqueId(), session.offer);
        p.showTitle(Title.title(
                Component.text("Возвращение выполнено", NamedTextColor.GREEN),
                Component.text("-" + result.cost + " рейтинга • баланс " + result.balance, NamedTextColor.GOLD),
                Title.Times.times(Duration.ofMillis(100), Duration.ofSeconds(2), Duration.ofMillis(400))
        ));
        p.sendMessage("§aTaskForge: §fвозврат выполнен. Новый Minecraft-баланс: §e" + result.balance);
    }

    private Location fallbackLocation(Player p, DeathRescueSession session) {
        if (session != null && session.returnLocation != null && session.returnLocation.getWorld() != null) {
            return session.returnLocation.clone();
        }
        Location respawn = p.getRespawnLocation();
        if (respawn != null && respawn.getWorld() != null) return respawn;
        return p.getWorld().getSpawnLocation();
    }

    private void teleportToFallback(Player p, DeathRescueSession session, java.util.function.Consumer<Boolean> completed) {
        Location primary = fallbackLocation(p, session);
        p.teleportAsync(primary).thenAccept(ok -> p.getScheduler().run(this, task -> {
            if (!p.isOnline()) return;
            if (ok) {
                completed.accept(true);
                return;
            }
            Location spawn = p.getWorld().getSpawnLocation();
            p.teleportAsync(spawn).thenAccept(spawnOk -> p.getScheduler().run(this, next -> {
                if (!p.isOnline()) return;
                completed.accept(spawnOk);
            }, null));
        }, null));
    }

    private void cancelRescueWithoutCharge(Player p, DeathRescueSession session, String message) {
        session.offer.used = false;
        session.locked = false;
        session.purchasePending = false;
        teleportToFallback(p, session, returned -> {
            if (!returned) {
                p.sendMessage("§eTaskForge: §cне удалось вернуть тебя на точку респавна. Переподключись к серверу; рейтинг не списан.");
                return;
            }
            restoreRescueState(p, session);
            rescueSessions.remove(p.getUniqueId(), session);
            p.clearTitle();
            if (message != null && !message.isBlank()) p.sendMessage(message);
        });
    }

    private void restoreRescueState(Player p, DeathRescueSession session) {
        GameMode mode = session == null ? GameMode.SURVIVAL : session.originalMode;
        p.setGameMode(mode);
        p.getPersistentDataContainer().remove(rescueActiveKey);
        p.getPersistentDataContainer().remove(rescueOriginalModeKey);
    }

    private void recoverInterruptedRescue(Player p) {
        if (p == null) return;
        DeathRescueSession session = rescueSessions.get(p.getUniqueId());
        Byte active = p.getPersistentDataContainer().get(rescueActiveKey, PersistentDataType.BYTE);
        if (session == null && (active == null || active == 0)) return;
        if (session != null && session.purchasePending) {
            p.sendMessage("§eTaskForge: §7завершаем подтверждение возврата. Подожди несколько секунд.");
            return;
        }
        if (session != null && session.purchaseCompleted && session.finalLocation != null && isSafeLocation(session.finalLocation)) {
            p.teleportAsync(session.finalLocation).thenAccept(ok -> p.getScheduler().run(this, task -> {
                if (!p.isOnline()) return;
                if (!ok) {
                    p.sendMessage("§eTaskForge: §cоплаченный возврат не удалось завершить. Операцию можно восстановить в редакторе пользователя.");
                    return;
                }
                DeathTeleportPurchaseResponse result = session.purchaseResult;
                completePaidRescue(p, session, result == null ? new DeathTeleportPurchaseResponse() : result);
            }, null));
            return;
        }
        if (session != null) session.locked = false;
        String storedMode = p.getPersistentDataContainer().get(rescueOriginalModeKey, PersistentDataType.STRING);
        GameMode mode = GameMode.SURVIVAL;
        if (storedMode != null) {
            try { mode = GameMode.valueOf(storedMode); } catch (IllegalArgumentException ignored) {}
        }
        GameMode finalMode = mode;
        teleportToFallback(p, session, returned -> {
            if (!returned) {
                p.sendMessage("§eTaskForge: §cне удалось завершить восстановление режима. Переподключись ещё раз.");
                return;
            }
            p.setGameMode(finalMode);
            p.getPersistentDataContainer().remove(rescueActiveKey);
            p.getPersistentDataContainer().remove(rescueOriginalModeKey);
            if (session != null) {
                session.offer.used = false;
                rescueSessions.remove(p.getUniqueId(), session);
            }
            p.clearTitle();
            p.sendMessage("§eTaskForge: §7прерванный возврат отменён. Рейтинг не списан.");
        });
    }

    private void applyRecoveryProtection(Player p) {
        addRecoveryEffect(p, PotionEffectType.FIRE_RESISTANCE, deathTeleportFireResistanceSeconds, 0);
        addRecoveryEffect(p, PotionEffectType.RESISTANCE, deathTeleportResistanceSeconds, 4);
        addRecoveryEffect(p, PotionEffectType.REGENERATION, deathTeleportRegenerationSeconds, 1);
        addRecoveryEffect(p, PotionEffectType.SLOW_FALLING, deathTeleportSlowFallingSeconds, 0);
    }

    private void addRecoveryEffect(Player p, PotionEffectType type, int seconds, int amplifier) {
        if (seconds <= 0) return;
        p.addPotionEffect(new PotionEffect(type, seconds * 20, amplifier, true, true, true));
    }

    void forwardMinecraftChatAsync(Player p, String message) {
        if (!chatForwardPlayerMessages) return;
        forwardMinecraftEventAsync(p, message, "chat");
    }

    void forwardMinecraftEventAsync(Player p, String message, String kind) {
        if (!chatEnabled || !canCallTaskForge() || p == null || message == null || message.trim().isEmpty()) return;

        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/chat/bridge/incoming";
        String body = GSON.toJson(new ChatBridgeRequest(
                p.getName(),
                p.getUniqueId().toString(),
                message.trim(),
                kind == null ? "chat" : kind.trim()
        ));

        HttpRequest req = newTaskForgeRequest(url)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8))
                .build();

        httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .thenAccept(resp -> { if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess(); else markTaskForgeFailure("chat-push-http", null); })
                .exceptionally(ex -> {
                    markTaskForgeFailure("chat-push", ex);
                    return null;
                });
    }

    private void pollSiteChatSafe() {
        try {
            pollSiteChat();
        } catch (Exception ex) {
            markTaskForgeFailure("chat-poll", ex);
        }
    }

    private void pollSiteChat() throws Exception {
        if (!shouldPollSiteChatNow()) return;

        String after = java.net.URLEncoder.encode(chatCursorUtc.toString(), StandardCharsets.UTF_8);
        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/chat/bridge/pull?afterUtc=" + after + "&take=25";

        HttpRequest req = newTaskForgeRequest(url)
                .GET()
                .build();

        HttpResponse<String> resp = httpClient.send(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8));
        if (resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
        if (resp.statusCode() < 200 || resp.statusCode() >= 300) {
            markTaskForgeFailure("chat-poll-http", null);
            getLogger().warning("TaskForge chat poll HTTP status=" + resp.statusCode());
            return;
        }

        ChatMessageResponse[] items = GSON.fromJson(resp.body(), ChatMessageResponse[].class);
        if (items == null || items.length == 0) return;

        Instant maxSeen = chatCursorUtc;
        for (ChatMessageResponse item : items) {
            if (item == null || item.message == null || item.message.trim().isEmpty()) continue;
            if ("Minecraft".equalsIgnoreCase(item.source)) continue;

            String author = firstNonBlank(item.authorName, item.minecraftNick, "TaskForge");
            String line = firstNonBlank(chatSitePrefix, "§d[TaskForge]§r ") + author + ": " + item.message;
            for (Player pl : Bukkit.getOnlinePlayers()) {
                sendChat(pl, line);
            }

            if (item.createdAtUtc != null && !item.createdAtUtc.isBlank()) {
                try {
                    Instant ts = Instant.parse(item.createdAtUtc);
                    if (ts.isAfter(maxSeen)) maxSeen = ts;
                } catch (Exception ignored) {}
            }
        }
        chatCursorUtc = maxSeen;
    }

    static final class ChatBridgeRequest {
        final String nick;
        final String uuid;
        final String message;
        final String kind;
        ChatBridgeRequest(String nick, String uuid, String message, String kind) {
            this.nick = nick;
            this.uuid = uuid;
            this.message = message;
            this.kind = kind;
        }
    }

    static final class ChatMessageResponse {
        String source;
        String authorName;
        String minecraftNick;
        String message;
        String createdAtUtc;
    }

    private static final class JoinEventRequest {
        final String nick;
        final String uuid;
        JoinEventRequest(String nick, String uuid) {
            this.nick = nick;
            this.uuid = uuid;
        }
    }

    // Mirror of TaskForge MinecraftPlayerStatusDto (only fields we need)
    static final class PlayerStatusResponse {
        boolean linked;
        boolean debuffed;
        boolean chargedThisWeek;
        int score;
        int baseRating;
        int minecraftBalance;
        int balance;
        int minecraftSpent;
        int minecraftRestored;
        int minecraftAdjustment;
        int deathTeleportCost;
        int weeklyPenaltyCurrent;
        int penaltyTotal;
        int effectiveScore;
    }

    static final class DeathTeleportOffer {
        final String deathId;
        final String requestId;
        final String worldName;
        final double x;
        final double y;
        final double z;
        final float yaw;
        final float pitch;
        final Instant expiresAt;
        final int cost;
        volatile boolean used;

        DeathTeleportOffer(String deathId, String requestId, String worldName, double x, double y, double z, float yaw, float pitch, Instant expiresAt, int cost) {
            this.deathId = deathId;
            this.requestId = requestId;
            this.worldName = worldName;
            this.x = x;
            this.y = y;
            this.z = z;
            this.yaw = yaw;
            this.pitch = pitch;
            this.expiresAt = expiresAt;
            this.cost = cost;
        }
    }

    static final class DeathRescueSession {
        final DeathTeleportOffer offer;
        final GameMode originalMode;
        final Location returnLocation;
        final Location anchor;
        volatile int secondsLeft;
        volatile Location finalLocation;
        volatile boolean locked;
        volatile boolean purchasePending;
        volatile boolean purchaseCompleted;
        volatile DeathTeleportPurchaseResponse purchaseResult;

        DeathRescueSession(DeathTeleportOffer offer, GameMode originalMode, Location returnLocation, Location anchor, int secondsLeft) {
            this.offer = offer;
            this.originalMode = originalMode;
            this.returnLocation = returnLocation;
            this.anchor = anchor;
            this.secondsLeft = secondsLeft;
        }
    }

    static final class DeathTeleportQuoteRequest {
        final String nick;
        final String uuid;
        final String deathId;
        DeathTeleportQuoteRequest(String nick, String uuid, String deathId) {
            this.nick = nick;
            this.uuid = uuid;
            this.deathId = deathId;
        }
    }

    static final class DeathTeleportPurchaseRequest {
        final String nick;
        final String uuid;
        final String deathId;
        final String requestId;
        DeathTeleportPurchaseRequest(String nick, String uuid, String deathId, String requestId) {
            this.nick = nick;
            this.uuid = uuid;
            this.deathId = deathId;
            this.requestId = requestId;
        }
    }

    static final class DeathTeleportQuoteResponse {
        boolean linked;
        boolean allowed;
        String reason;
        int cost;
        int balance;
        int baseRating;
        int adjustmentTotal;
        int spentTotal;
        int restoredTotal;
    }

    static final class DeathTeleportPurchaseResponse {
        boolean success;
        boolean duplicate;
        String reason;
        int cost;
        int balance;
        int newBalance;
        int baseRating;
        int adjustmentTotal;
    }

    private static final class TfListener implements Listener {
        private final TaskForgeLinkPlugin plugin;
        TfListener(TaskForgeLinkPlugin plugin) { this.plugin = plugin; }

        @EventHandler
        public void onJoin(PlayerJoinEvent e) {
            Player p = e.getPlayer();
            if (p != null) plugin.recoverInterruptedRescue(p);
            if (p != null && !plugin.isExempt(p) && plugin.chatForwardJoinQuit) {
                plugin.forwardMinecraftEventAsync(p, p.getName() + " зашёл на сервер", "join");
            }

            if (p == null || plugin.isExempt(p) || !plugin.canCallTaskForge()) {
                return;
            }

            plugin.notifyJoinAsync(p).thenAcceptAsync(st -> {
                if (st == null) return;
                if (!st.linked) {
                    plugin.sendChat(p, "§eTaskForge: §7можно привязать Minecraft в профиле сайта и тратить рейтинг на полезные действия в игре.");
                    return;
                }
                int balance = st.minecraftBalance;
                int cost = st.deathTeleportCost > 0 ? st.deathTeleportCost : plugin.deathTeleportCost;
                plugin.sendChat(p, "§eTaskForge: §7Minecraft-баланс: §e" + balance + "§7. Возврат на место смерти стоит §e" + cost + "§7.");
            }, plugin.tfExecutor);
        }

        @EventHandler
        public void onQuit(PlayerQuitEvent e) {
            Player p = e.getPlayer();
            if (p == null) return;
            if (!plugin.isExempt(p) && plugin.chatForwardJoinQuit) {
                plugin.forwardMinecraftEventAsync(p, p.getName() + " вышел с сервера", "quit");
            }
        }

        @EventHandler
        public void onAdvancement(PlayerAdvancementDoneEvent e) {
            Player p = e.getPlayer();
            if (p == null || plugin.isExempt(p)) return;
            String key = e.getAdvancement() != null && e.getAdvancement().getKey() != null
                    ? e.getAdvancement().getKey().getKey()
                    : "advancement";
            if (!plugin.shouldForwardAdvancementKey(key)) return;
            plugin.forwardMinecraftEventAsync(p, p.getName() + " получил достижение: " + key, "advancement");
        }

        @EventHandler
        public void onChat(AsyncPlayerChatEvent e) {
            Player p = e.getPlayer();
            if (p == null) return;
            if (plugin.isExempt(p) || !plugin.chatForwardPlayerMessages) return;
            plugin.forwardMinecraftChatAsync(p, e.getMessage());
        }

        @EventHandler
        public void onMove(org.bukkit.event.player.PlayerMoveEvent e) {
            Player p = e.getPlayer();
            DeathRescueSession session = plugin.rescueSessions.get(p.getUniqueId());
            if (session == null || !session.locked || session.finalLocation == null || e.getTo() == null) return;
            Location to = e.getTo();
            Location target = session.finalLocation;
            if (to.getWorld() == null || target.getWorld() == null || !to.getWorld().getUID().equals(target.getWorld().getUID())
                    || to.distanceSquared(target) > 0.01) {
                Location locked = target.clone();
                locked.setYaw(to.getYaw());
                locked.setPitch(to.getPitch());
                e.setTo(locked);
            }
        }

        @EventHandler
        public void onDeath(PlayerDeathEvent e) {
            Player p = e.getEntity();
            if (p == null || plugin.isExempt(p)) return;
            plugin.rememberDeathLocation(p, p.getLocation());
        }

        @EventHandler
        public void onRespawn(PlayerRespawnEvent e) {
            Player p = e.getPlayer();
            if (p == null || plugin.isExempt(p)) return;
            p.getScheduler().runDelayed(plugin, task -> plugin.showDeathTeleportOffer(p), null, plugin.deathTeleportMessageDelayTicks);
        }
    }

    private static final class SendCodeHandler implements HttpHandler {
        private final TaskForgeLinkPlugin plugin;
        private final String sharedKey;
        private final List<String> allowedIps;

        private SendCodeHandler(TaskForgeLinkPlugin plugin, String sharedKey, List<String> allowedIps) {
            this.plugin = plugin;
            this.sharedKey = sharedKey == null ? "" : sharedKey;
            this.allowedIps = allowedIps;
        }

        @Override
        public void handle(HttpExchange ex) throws IOException {
            try {
                final String remoteIp = ex.getRemoteAddress().getAddress().getHostAddress();
                final String method = ex.getRequestMethod();
                final String uri = String.valueOf(ex.getRequestURI());
                // Логируем вообще все входящие, чтобы быстро понять "дошло ли".
                plugin.getLogger().info("[TF->MC] request received ip=" + remoteIp + " method=" + method + " uri=" + uri);

                if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) {
                    writeJson(ex, 405, "{\"error\":\"method_not_allowed\"}");
                    return;
                }

                // IP allowlist (если задана)
                if (allowedIps != null && !allowedIps.isEmpty()) {
                    if (allowedIps.stream().noneMatch(ip -> Objects.equals(ip, remoteIp))) {
                        plugin.getLogger().warning("[TF->MC] forbidden by IP allowlist ip=" + remoteIp);
                        writeJson(ex, 403, "{\"error\":\"forbidden\"}");
                        return;
                    }
                }

                Headers h = ex.getRequestHeaders();

                String gotKey = Optional.ofNullable(h.getFirst("X-TaskForge-Key")).orElse("");
                if (sharedKey.isBlank() || !sharedKey.equals(gotKey)) {
                    // Не палим ключ, но логируем диагностические признаки.
                    plugin.getLogger().warning(
                            "[TF->MC] unauthorized ip=" + remoteIp +
                                    " hasHeader=" + (!gotKey.isBlank()) +
                                    " gotLen=" + gotKey.length() +
                                    " expectedLen=" + sharedKey.length());
                    writeJson(ex, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }

                String requestId = Optional.ofNullable(h.getFirst("X-Request-Id")).orElse("");
                if (!requestId.isBlank()) {
                    boolean first = plugin.markRequestIdOnce(requestId);
                    if (!first) {
                        plugin.getLogger().info("[TF->MC] duplicate requestId=" + requestId + " ip=" + remoteIp);
                        writeJson(ex, 409, "{\"delivered\":true,\"duplicate\":true}");
                        return;
                    }
                }

                String body = readAll(ex.getRequestBody());

                SendCodeRequest req;
                try {
                    req = GSON.fromJson(body, SendCodeRequest.class);
                } catch (JsonSyntaxException jse) {
                    writeJson(ex, 400, "{\"error\":\"bad_json\"}");
                    return;
                }

                if (req == null || req.nick == null || req.code == null) {
                    writeJson(ex, 400, "{\"error\":\"bad_request\"}");
                    return;
                }

                String nick = req.nick.trim();
                String code = req.code.trim();

                Player p = Bukkit.getPlayerExact(nick);
                if (p == null) {
                    // иногда ник может отличаться регистром
                    for (Player pl : Bukkit.getOnlinePlayers()) {
                        if (pl.getName().equalsIgnoreCase(nick)) {
                            p = pl;
                            break;
                        }
                    }
                }

                if (p == null) {
                    plugin.getLogger().info("[TF->MC] player offline nick=" + nick + " ip=" + remoteIp);
                    writeJson(ex, 404, "{\"delivered\":false,\"online\":false,\"reason\":\"offline\"}");
                    return;
                }

                Player finalP = p;
                // Folia-safe: отправка сообщения через scheduler игрока
                finalP.getScheduler().run(plugin, task -> {
                    finalP.sendMessage("\u00A7a\u2714 \u00A7fКод привязки TaskForge: \u00A7e" + code);
                    finalP.sendMessage("\u00A77Введи его на сайте в профиле. Код действует примерно 10 минут.");
                }, null);

                String uuid = finalP.getUniqueId().toString();
                plugin.getLogger().info("[TF->MC] code delivered nick=" + finalP.getName() + " uuid=" + uuid + " ip=" + remoteIp);
                writeJson(ex, 200, "{\"delivered\":true,\"online\":true,\"uuid\":\"" + uuid + "\"}");
            } catch (Exception e) {
                plugin.getLogger().severe("HTTP handler error: " + e.getMessage());
                writeJson(ex, 500, "{\"error\":\"server_error\"}");
            }
        }

        private static void writeJson(HttpExchange ex, int status, String json) throws IOException {
            byte[] bytes = json.getBytes(StandardCharsets.UTF_8);
            ex.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
            ex.getResponseHeaders().set("Connection", "close");
            ex.sendResponseHeaders(status, bytes.length);
            try (OutputStream os = ex.getResponseBody()) {
                os.write(bytes);
            }
        }

        private static String readAll(InputStream is) throws IOException {
            return new String(is.readAllBytes(), StandardCharsets.UTF_8);
        }

        private static final class SendCodeRequest {
            String nick;
            String code;
            Integer ttlSeconds;
        }
    }
}
