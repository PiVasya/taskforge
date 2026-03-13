package com.taskforge.folia;

import com.google.gson.Gson;
import com.google.gson.JsonSyntaxException;
import com.sun.net.httpserver.Headers;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;
import com.sun.net.httpserver.HttpServer;
import org.bukkit.Bukkit;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.PlayerDeathEvent;
import org.bukkit.event.player.AsyncPlayerChatEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.event.player.PlayerAdvancementDoneEvent;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
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
    private boolean debuffsEnabled;
    private int debuffDurationSeconds;
    private boolean chatEnabled;
    private int chatPollIntervalSeconds;
    private String chatSitePrefix;
    private String chatMinecraftPrefix;
    private volatile Instant chatCursorUtc = Instant.EPOCH;

    // Игроки-исключения (чтобы не было запросов в API и не было дебаффов)
    private final Set<String> exemptNicksLower = ConcurrentHashMap.newKeySet();
    private final Set<String> exemptUuidsLower = ConcurrentHashMap.newKeySet();

    // Soft enforcement: first problematic join -> warning only, no debuffs.
    // Next join (if still problematic) -> debuffs.
    private final java.util.Map<java.util.UUID, Boolean> warnedOnce = new java.util.concurrent.ConcurrentHashMap<>();

    private final java.util.Set<java.util.UUID> graceOnline = java.util.concurrent.ConcurrentHashMap.newKeySet();

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
        debuffsEnabled = getConfig().getBoolean("debuffs.enabled", true);
        debuffDurationSeconds = Math.max(30, getConfig().getInt("debuffs.durationSeconds", 600));
        chatEnabled = getConfig().getBoolean("chat.enabled", true);
        chatPollIntervalSeconds = Math.max(2, getConfig().getInt("chat.pollIntervalSeconds", 3));
        chatSitePrefix = getConfig().getString("chat.sitePrefix", "§d[TaskForge]§r ");
        chatMinecraftPrefix = getConfig().getString("chat.minecraftPrefix", "[MC] ");
        chatCursorUtc = Instant.now();

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
                String resp = "{\"ok\":true}";
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

        // Listener: plugin -> TaskForge (weekly join + debuffs)
        tfExecutor = Executors.newFixedThreadPool(2, r -> {
            Thread t = new Thread(r, "taskforge-link-http");
            t.setDaemon(true);
            return t;
        });

        Bukkit.getPluginManager().registerEvents(new TfListener(this), this);

        // Очистка кеша requestId, чтобы не рос бесконечно
        janitor = Executors.newSingleThreadScheduledExecutor(r -> {
            Thread t = new Thread(r, "taskforge-link-janitor");
            t.setDaemon(true);
            return t;
        });
        janitor.scheduleAtFixedRate(this::cleanupRequestCache, 5, 5, TimeUnit.MINUTES);
        if (chatEnabled && canCallTaskForge()) {
            janitor.scheduleAtFixedRate(this::pollSiteChatSafe, 3, chatPollIntervalSeconds, TimeUnit.SECONDS);
        }
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

    void applyDebuffs(Player p, boolean debuffed) {
        if (!debuffsEnabled) return;

        // "Infinite" debuffs: duration in Bukkit is int ticks.
        // Integer.MAX_VALUE ticks is effectively permanent. We re-check on join/death
        // to remove debuffs if the player becomes eligible.
        int ticks = Integer.MAX_VALUE;
        p.getScheduler().run(this, task -> {
            if (!p.isOnline()) return;

            if (debuffed) {
                p.addPotionEffect(new PotionEffect(PotionEffectType.SLOWNESS, ticks, 1, true, false, false));
                p.addPotionEffect(new PotionEffect(PotionEffectType.BLINDNESS, ticks, 0, true, false, false));
                p.addPotionEffect(new PotionEffect(PotionEffectType.MINING_FATIGUE, ticks, 1, true, false, false));
            } else {
                p.removePotionEffect(PotionEffectType.SLOWNESS);
                p.removePotionEffect(PotionEffectType.BLINDNESS);
                p.removePotionEffect(PotionEffectType.MINING_FATIGUE);
            }
        }, null);
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

    void forwardMinecraftChatAsync(Player p, String message) {
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
        if (!chatEnabled || !canCallTaskForge()) return;

        String after = java.net.URLEncoder.encode(chatCursorUtc.toString(), StandardCharsets.UTF_8);
        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/chat/bridge/pull?afterUtc=" + after + "&take=50";

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
        int weeklyPenalty;
        int penaltyTotal;
        int effectiveScore;
    }

    private static final class TfListener implements Listener {
        private final TaskForgeLinkPlugin plugin;
        TfListener(TaskForgeLinkPlugin plugin) { this.plugin = plugin; }

        @EventHandler
        public void onJoin(PlayerJoinEvent e) {
            Player p = e.getPlayer();
            if (p != null && !plugin.isExempt(p)) {
                plugin.forwardMinecraftEventAsync(p, p.getName() + " зашёл на сервер", "join");
            }

            // Исключения: не пишем в TaskForge и гарантируем отсутствие дебаффов.
            if (plugin.isExempt(p)) {
                plugin.getLogger().info("[TF] join ignored (exempt) nick=" + p.getName() + " uuid=" + p.getUniqueId());
                plugin.applyDebuffs(p, false);
                return;
            }

            // Если TaskForge не настроен — просто ничего не делаем
            if (!plugin.canCallTaskForge()) return;

            plugin.notifyJoinAsync(p).thenAcceptAsync(st -> {
                final boolean linked = st != null && st.linked;

                // API реально недоступен/сломался — не наказываем игрока вслепую.
                if (st == null) {
                    plugin.sendChat(p, "§eTaskForge: §cне удалось проверить статус (API недоступен). Попробуй позже.");
                    plugin.applyDebuffs(p, false);
                    return;
                }

                // Optional info about the weekly penalty.
                if (st.chargedThisWeek) {
                    p.getScheduler().run(plugin, t -> p.sendMessage("§eTaskForge: на этой неделе вход засчитан (-" + st.weeklyPenalty + ")"), null);
                }

                // Незалинкованный игрок должен получать дебафф сразу.
                if (!linked) {
                    plugin.graceOnline.remove(p.getUniqueId());
                    plugin.warnedOnce.remove(p.getUniqueId());
                    plugin.sendChat(p, "§eTaskForge: §6привяжи аккаунт на сайте TaskForge (профиль → Minecraft). Пока аккаунт не привязан, действует ограничение.");
                    plugin.applyDebuffs(p, true);
                    return;
                }

                // Всё хорошо — точно снимаем дебаффы и чистим grace.
                if (!st.debuffed) {
                    plugin.graceOnline.remove(p.getUniqueId());
                    plugin.warnedOnce.remove(p.getUniqueId());
                    plugin.applyDebuffs(p, false);
                    return;
                }

                plugin.sendChat(p,
                    "§eTaskForge: §cнедостаточно рейтинга для сервера. §7Рейтинг=" + st.score +
                    " штраф=" + st.penaltyTotal + " итог=" + st.effectiveScore +
                    " (нужно ≥0). Решай задачи и зайди снова.");

                // Soft mode оставляем только для случая нехватки рейтинга.
                if (plugin.warnedOnce.putIfAbsent(p.getUniqueId(), true) == null) {
                    plugin.graceOnline.add(p.getUniqueId());
                    plugin.applyDebuffs(p, false);
                    return;
                }

                plugin.graceOnline.remove(p.getUniqueId());
                plugin.applyDebuffs(p, true);
            }, plugin.tfExecutor);
        }

        @EventHandler
        public void onQuit(PlayerQuitEvent e) {
            Player p = e.getPlayer();
            if (p == null) return;
            if (!plugin.isExempt(p)) {
                plugin.forwardMinecraftEventAsync(p, p.getName() + " вышел с сервера", "quit");
            }
            plugin.graceOnline.remove(p.getUniqueId());
        }

        @EventHandler
        public void onAdvancement(PlayerAdvancementDoneEvent e) {
            Player p = e.getPlayer();
            if (p == null || plugin.isExempt(p)) return;
            String key = e.getAdvancement() != null && e.getAdvancement().getKey() != null
                    ? e.getAdvancement().getKey().getKey()
                    : "advancement";
            plugin.forwardMinecraftEventAsync(p, p.getName() + " получил достижение: " + key, "advancement");
        }

        @EventHandler
        public void onChat(AsyncPlayerChatEvent e) {
            Player p = e.getPlayer();
            if (p == null) return;
            if (plugin.isExempt(p)) return;
            plugin.forwardMinecraftChatAsync(p, e.getMessage());
        }

        @EventHandler
        public void onDeath(PlayerDeathEvent e) {
            Player p = e.getEntity();

            if (plugin.isExempt(p)) {
                plugin.applyDebuffs(p, false);
                return;
            }
            if (!plugin.canCallTaskForge()) return;

            plugin.getStatusAsync(p).thenAcceptAsync(st -> {
                // Death re-check: if player is in the grace window (first warning join), do nothing.
                if (plugin.graceOnline.contains(p.getUniqueId())) return;

                if (st == null) {
                    plugin.applyDebuffs(p, false);
                    return;
                }

                final boolean linked = st.linked;
                final boolean shouldDebuff = !linked || st.debuffed;
                plugin.applyDebuffs(p, shouldDebuff);
            }, plugin.tfExecutor);
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
