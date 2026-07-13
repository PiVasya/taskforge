package com.taskforge.folia;

import com.google.gson.Gson;
import com.google.gson.JsonSyntaxException;
import com.sun.net.httpserver.Headers;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;
import com.sun.net.httpserver.HttpServer;
import org.bukkit.Bukkit;
import org.bukkit.command.Command;
import org.bukkit.command.CommandSender;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.player.AsyncPlayerChatEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.event.player.PlayerAdvancementDoneEvent;
import org.bukkit.plugin.java.JavaPlugin;

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
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HexFormat;
import java.time.Duration;
import java.time.Instant;
import java.util.Locale;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

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
    private final ConcurrentHashMap<UUID, Player> onlinePlayers = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<String, UUID> onlinePlayersByName = new ConcurrentHashMap<>();
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
    private volatile Instant chatCursorUtc = Instant.EPOCH;
    private int deathCoordinatesCost;
    private int deathChestCost;
    private int deathTeleportCost;
    private DeathRecoveryManager deathRecoveryManager;

    private boolean debugEnabled;
    private boolean debugHttp;
    private boolean debugHttpBodies;
    private boolean debugDeathRecovery;
    private boolean debugScheduler;
    private boolean debugJournal;
    private boolean debugHeartbeat;
    private int debugConnectivityProbeSeconds;
    private final AtomicLong httpSequence = new AtomicLong();

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

    boolean debugEnabled() { return debugEnabled; }
    boolean debugDeathRecovery() { return debugEnabled && debugDeathRecovery; }
    boolean debugScheduler() { return debugEnabled && debugScheduler; }
    boolean debugJournal() { return debugEnabled && debugJournal; }
    boolean debugHeartbeat() { return debugEnabled && debugHeartbeat; }

    void debug(String area, String message) {
        if (!debugEnabled) return;
        getLogger().info("[DEBUG][" + area + "] " + message);
    }

    void debugDeath(String message) {
        if (debugDeathRecovery()) debug("death", message);
    }

    void debugScheduler(String message) {
        if (debugScheduler()) debug("scheduler", message);
    }

    void debugJournal(String message) {
        if (debugJournal()) debug("journal", message);
    }

    private String bodyPreview(String body) {
        if (!debugHttpBodies || body == null || body.isEmpty()) return body == null || body.isEmpty() ? "<empty>" : "<hidden len=" + body.length() + ">";
        String normalized = body.replace('\n', ' ').replace('\r', ' ');
        return normalized.length() > 1000 ? normalized.substring(0, 1000) + "...<truncated>" : normalized;
    }

    private static String keyFingerprint(String value) {
        if (isBlank(value)) return "missing";
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
            return HexFormat.of().formatHex(digest, 0, 6);
        } catch (NoSuchAlgorithmException impossible) {
            return "sha256-unavailable";
        }
    }

    private static Throwable unwrap(Throwable error) {
        Throwable current = error;
        while ((current instanceof CompletionException || current instanceof ExecutionException) && current.getCause() != null) {
            current = current.getCause();
        }
        return current;
    }

    private void logHttpRequest(long id, String scope, HttpRequest request, String body) {
        if (!debugEnabled || !debugHttp) return;
        debug("http", "#" + id + " -> " + scope + " " + request.method() + " " + request.uri()
                + " timeout=" + request.timeout().map(Duration::toMillis).orElse(-1L) + "ms body=" + bodyPreview(body));
    }

    private void logHttpResponse(long id, String scope, HttpResponse<String> response) {
        if (!debugEnabled || !debugHttp) return;
        if (response == null) {
            debug("http", "#" + id + " <- " + scope + " null response");
            return;
        }
        debug("http", "#" + id + " <- " + scope + " status=" + response.statusCode()
                + " uri=" + response.uri() + " body=" + bodyPreview(response.body()));
    }

    private void logHttpException(long id, String scope, Throwable error) {
        Throwable root = unwrap(error);
        getLogger().log(java.util.logging.Level.WARNING, "[DEBUG][http] #" + id + " !! " + scope
                + " " + root.getClass().getName() + ": " + root.getMessage(), root);
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
        Throwable root = ex == null ? null : unwrap(ex);
        String msg = root == null ? "HTTP response was not successful; see preceding [DEBUG][http] response"
                : root.getClass().getSimpleName() + ": " + root.getMessage();
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
                .header("Accept", "application/json")
                .header("User-Agent", "TaskForgeLink/" + getDescription().getVersion())
                .header("X-Request-Id", UUID.randomUUID().toString())
                .header("X-Minecraft-Key", taskForgeKey);
    }

    @Override
    public void onEnable() {
        saveDefaultConfig();

        // Backward/typo compatibility:
        // Some configs may contain wrong-cased keys like `taskforgekey`.
        // We read both and auto-migrate to the canonical camelCase keys.
        migrateConfigKeys();

        debugEnabled = getConfig().getBoolean("debug.enabled", true);
        debugHttp = getConfig().getBoolean("debug.http", true);
        debugHttpBodies = getConfig().getBoolean("debug.httpBodies", true);
        debugDeathRecovery = getConfig().getBoolean("debug.deathRecovery", true);
        debugScheduler = getConfig().getBoolean("debug.scheduler", true);
        debugJournal = getConfig().getBoolean("debug.journal", true);
        debugHeartbeat = getConfig().getBoolean("debug.heartbeat", true);
        debugConnectivityProbeSeconds = Math.max(10, getConfig().getInt("debug.connectivityProbeSeconds", 30));

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
        chatCursorUtc = Instant.now();
        deathCoordinatesCost = Math.max(1, getConfig().getInt("deathRecovery.coordinatesCost", 10));
        deathChestCost = Math.max(1, getConfig().getInt("deathRecovery.chestCost", 50));
        deathTeleportCost = Math.max(1, getConfig().getInt("deathRecovery.teleportCost", 100));

        getLogger().info("[TaskForgeLink] development diagnostics enabled=" + debugEnabled
                + " http=" + debugHttp + " httpBodies=" + debugHttpBodies
                + " deathRecovery=" + debugDeathRecovery + " scheduler=" + debugScheduler
                + " journal=" + debugJournal + " heartbeat=" + debugHeartbeat);
        getLogger().info("[TaskForgeLink] backend baseUrl=" + (taskForgeBaseUrl.isBlank() ? "<missing>" : taskForgeBaseUrl)
                + " pluginKey=" + keyFingerprint(taskForgeKey) + " len=" + taskForgeKey.length()
                + " webhookKey=" + keyFingerprint(key) + " len=" + key.length()
                + " timeoutSeconds=" + taskForgeTimeoutSeconds);
        getLogger().info("[TaskForgeLink] death costs coordinates=" + deathCoordinatesCost
                + " chest=" + deathChestCost + " teleport=" + deathTeleportCost);
        if (!canCallTaskForge()) {
            getLogger().warning("[TaskForgeLink] Minecraft -> TaskForge calls are disabled because apiBaseUrl or pluginKey is empty.");
        }
        if ("CHANGE_ME".equals(key) || "CHANGE_ME".equals(taskForgeKey)) {
            getLogger().severe("[TaskForgeLink] CHANGE_ME is still present in a security key. Replace it before exposing the server.");
        }

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
                int onlinePlayerCount = onlinePlayerCount();
                debug("http-in", "health request ip=" + ex.getRemoteAddress() + " method=" + ex.getRequestMethod()
                        + " onlinePlayers=" + onlinePlayerCount);
                String resp = "{\"ok\":true,\"onlinePlayers\":" + onlinePlayerCount + "}";
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

        for (Player online : Bukkit.getOnlinePlayers()) {
            trackOnline(online);
        }
        Bukkit.getPluginManager().registerEvents(new TfListener(this), this);
        deathRecoveryManager = new DeathRecoveryManager(this);
        deathRecoveryManager.enable();

        // Очистка кеша requestId, чтобы не рос бесконечно
        janitor = Executors.newSingleThreadScheduledExecutor(r -> {
            Thread t = new Thread(r, "taskforge-link-janitor");
            t.setDaemon(true);
            return t;
        });
        janitor.scheduleAtFixedRate(this::cleanupRequestCache, 5, 5, TimeUnit.MINUTES);
        if (canCallTaskForge()) {
            janitor.scheduleAtFixedRate(this::probeTaskForgeConnectivitySafe, 0, debugConnectivityProbeSeconds, TimeUnit.SECONDS);
        }
        if (chatEnabled && canCallTaskForge()) {
            janitor.scheduleAtFixedRate(this::pollSiteChatSafe, chatPollIntervalSeconds, chatPollIntervalSeconds, TimeUnit.SECONDS);
        }
        getLogger().info("[TaskForgeLink] enabled; trackedOnlinePlayers=" + onlinePlayerCount()
                + " connectivityProbeSeconds=" + debugConnectivityProbeSeconds);
    }

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        if (!"tfback".equalsIgnoreCase(command.getName())) return false;
        sender.sendMessage("TaskForge: после смерти используй кнопки [Координаты], [Сундук], [Вернуться], [Сундук + возврат] или [Обычный дроп].");
        return true;
    }

    @Override
    public void onDisable() {
        getLogger().info("[TaskForgeLink] disabling; onlinePlayers=" + onlinePlayerCount()
                + " seenRequestIds=" + seenRequestIds.size());
        if (deathRecoveryManager != null) {
            deathRecoveryManager.disable();
            deathRecoveryManager = null;
        }
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
        onlinePlayers.clear();
        onlinePlayersByName.clear();
    }

    private void cleanupRequestCache() {
        Instant now = Instant.now();
        for (Map.Entry<String, Instant> e : seenRequestIds.entrySet()) {
            if (Duration.between(e.getValue(), now).toMinutes() >= 30) {
                seenRequestIds.remove(e.getKey());
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

        long httpId = httpSequence.incrementAndGet();
        logHttpRequest(httpId, "join", req, body);
        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .handle((resp, error) -> {
                    if (error != null) {
                        logHttpException(httpId, "join", error);
                        markTaskForgeFailure("join", error);
                        return null;
                    }
                    logHttpResponse(httpId, "join", resp);
                    if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
                    else markTaskForgeFailure("join-http", null);
                    return parseStatusResponse(resp);
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

        long httpId = httpSequence.incrementAndGet();
        logHttpRequest(httpId, "status", req, null);
        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .handle((resp, error) -> {
                    if (error != null) {
                        logHttpException(httpId, "status", error);
                        markTaskForgeFailure("status", error);
                        return null;
                    }
                    logHttpResponse(httpId, "status", resp);
                    if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
                    else markTaskForgeFailure("status-http", null);
                    return parseStatusResponse(resp);
                });
    }

    private boolean shouldPollSiteChatNow() {
        if (!chatEnabled || !canCallTaskForge()) return false;
        if (!chatPollOnlyWhenPlayersOnline) return true;
        return onlinePlayerCount() > 0;
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
        debugScheduler("queue chat player=" + p.getName() + " uuid=" + p.getUniqueId() + " len=" + message.length());
        p.getScheduler().run(this, task -> {
            if (!p.isOnline()) {
                debugScheduler("skip chat because player went offline uuid=" + p.getUniqueId());
                return;
            }
            p.sendMessage(message);
            debugScheduler("chat delivered player=" + p.getName() + " uuid=" + p.getUniqueId());
        }, () -> debugScheduler("chat scheduler retired before delivery uuid=" + p.getUniqueId()));
    }

    boolean isExempt(Player p) {
        if (p == null) return false;
        String nick = p.getName() == null ? "" : p.getName().trim().toLowerCase(Locale.ROOT);
        if (!nick.isEmpty() && exemptNicksLower.contains(nick)) return true;

        String uuid = p.getUniqueId() == null ? "" : p.getUniqueId().toString().trim().toLowerCase(Locale.ROOT);
        return !uuid.isEmpty() && exemptUuidsLower.contains(uuid);
    }

    void trackOnline(Player player) {
        if (player == null) return;
        UUID id = player.getUniqueId();
        onlinePlayers.put(id, player);
        debug("player", "track online name=" + player.getName() + " uuid=" + id + " dead=" + player.isDead());
        String name = player.getName();
        if (name != null && !name.isBlank()) {
            onlinePlayersByName.put(name.toLowerCase(Locale.ROOT), id);
        }
    }

    void trackOffline(Player player) {
        if (player == null) return;
        UUID id = player.getUniqueId();
        onlinePlayers.remove(id, player);
        debug("player", "track offline name=" + player.getName() + " uuid=" + id);
        String name = player.getName();
        if (name != null && !name.isBlank()) {
            onlinePlayersByName.remove(name.toLowerCase(Locale.ROOT), id);
        }
    }

    Player findOnlinePlayer(UUID playerId) {
        return playerId == null ? null : onlinePlayers.get(playerId);
    }

    Player findOnlinePlayer(String playerName) {
        if (playerName == null || playerName.isBlank()) return null;
        UUID id = onlinePlayersByName.get(playerName.trim().toLowerCase(Locale.ROOT));
        return id == null ? null : onlinePlayers.get(id);
    }

    int onlinePlayerCount() {
        return onlinePlayers.size();
    }

    List<Player> onlinePlayersSnapshot() {
        return List.copyOf(onlinePlayers.values());
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

        long httpId = httpSequence.incrementAndGet();
        String scope = "chat-push-" + (kind == null ? "chat" : kind.trim());
        logHttpRequest(httpId, scope, req, body);
        httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .whenComplete((resp, error) -> {
                    if (error != null) {
                        logHttpException(httpId, scope, error);
                        markTaskForgeFailure(scope, error);
                        return;
                    }
                    logHttpResponse(httpId, scope, resp);
                    if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
                    else markTaskForgeFailure(scope + "-http", null);
                });
    }

    private void probeTaskForgeConnectivitySafe() {
        if (!canCallTaskForge()) return;
        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/death-recovery/health";
        HttpRequest request = newTaskForgeRequest(url).GET().build();
        long id = httpSequence.incrementAndGet();
        logHttpRequest(id, "connectivity-probe", request, null);
        httpClient.sendAsync(request, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .whenComplete((response, error) -> {
                    if (error != null) {
                        logHttpException(id, "connectivity-probe", error);
                        markTaskForgeFailure("connectivity-probe", error);
                        return;
                    }
                    logHttpResponse(id, "connectivity-probe", response);
                    if (response != null && response.statusCode() >= 200 && response.statusCode() < 300) {
                        markTaskForgeSuccess();
                    } else {
                        markTaskForgeFailure("connectivity-probe-http", null);
                    }
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

        long httpId = httpSequence.incrementAndGet();
        logHttpRequest(httpId, "chat-poll", req, null);
        HttpResponse<String> resp;
        try {
            resp = httpClient.send(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8));
        } catch (Exception error) {
            logHttpException(httpId, "chat-poll", error);
            throw error;
        }
        logHttpResponse(httpId, "chat-poll", resp);
        if (resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
        if (resp.statusCode() < 200 || resp.statusCode() >= 300) {
            markTaskForgeFailure("chat-poll-http", null);
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
            for (Player pl : onlinePlayersSnapshot()) {
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
        int score;
        int baseRating;
        int minecraftBalance;
        int balance;
        int minecraftSpent;
        int minecraftRestored;
        int minecraftAdjustment;
        int deathCoordinatesCost;
        int deathChestCost;
        int deathTeleportCost;
        int effectiveScore;
    }

    private static final class TfListener implements Listener {
        private final TaskForgeLinkPlugin plugin;
        TfListener(TaskForgeLinkPlugin plugin) { this.plugin = plugin; }

        @EventHandler
        public void onJoin(PlayerJoinEvent e) {
            Player p = e.getPlayer();
            if (p != null) plugin.trackOnline(p);
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
                int coordinatesCost = st.deathCoordinatesCost > 0 ? st.deathCoordinatesCost : plugin.deathCoordinatesCost;
                int chestCost = st.deathChestCost > 0 ? st.deathChestCost : plugin.deathChestCost;
                int returnCost = st.deathTeleportCost > 0 ? st.deathTeleportCost : plugin.deathTeleportCost;
                plugin.sendChat(p, "§eTaskForge: §7Minecraft-баланс: §e" + balance
                        + "§7. Координаты: §e" + coordinatesCost + "§7, сундук: §e" + chestCost + "§7, возврат: §e" + returnCost + "§7.");
            }, plugin.tfExecutor);
        }

        @EventHandler
        public void onQuit(PlayerQuitEvent e) {
            Player p = e.getPlayer();
            if (p == null) return;
            if (!plugin.isExempt(p) && plugin.chatForwardJoinQuit) {
                plugin.forwardMinecraftEventAsync(p, p.getName() + " вышел с сервера", "quit");
            }
            plugin.trackOffline(p);
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
                                    " gotFp=" + keyFingerprint(gotKey) +
                                    " expectedLen=" + sharedKey.length() +
                                    " expectedFp=" + keyFingerprint(sharedKey));
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

                Player p = plugin.findOnlinePlayer(nick);

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
                plugin.getLogger().log(java.util.logging.Level.SEVERE, "[TF->MC] HTTP handler error", e);
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
