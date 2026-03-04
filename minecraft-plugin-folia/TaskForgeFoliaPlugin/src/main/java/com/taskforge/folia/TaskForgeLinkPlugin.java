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
import org.bukkit.event.player.PlayerJoinEvent;
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
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Optional;
import java.util.concurrent.*;

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
    private final ConcurrentHashMap<String, Instant> seenRequestIds = new ConcurrentHashMap<>();
    private ScheduledExecutorService janitor;

    private final HttpClient httpClient = HttpClient.newBuilder()
            .version(HttpClient.Version.HTTP_1_1)
            .build();

    private ExecutorService tfExecutor;
    private String taskForgeBaseUrl;
    private String taskForgeKey;
    private int taskForgeTimeoutSeconds;
    private boolean debuffsEnabled;
    private int debuffDurationSeconds;

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

        try {
            InetAddress addr = InetAddress.getByName(host);
            server = HttpServer.create(new InetSocketAddress(addr, port), 0);
            server.createContext(path, new SendCodeHandler(this, key, allowedIps));
            server.setExecutor(Executors.newFixedThreadPool(2));
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
    }

    @Override
    public void onDisable() {
        if (server != null) {
            server.stop(0);
            server = null;
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

        HttpRequest req = HttpRequest.newBuilder()
                .uri(URI.create(url))
                .timeout(Duration.ofSeconds(taskForgeTimeoutSeconds))
                .header("Content-Type", "application/json")
                .header("X-Minecraft-Key", taskForgeKey)
                .POST(HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8))
                .build();

        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .thenApply(resp -> parseStatusResponse(resp))
                .exceptionally(ex -> {
                    getLogger().warning("TaskForge join call failed: " + ex.getMessage());
                    return null;
                });
    }

    CompletableFuture<PlayerStatusResponse> getStatusAsync(Player p) {
        if (!canCallTaskForge()) {
            return CompletableFuture.completedFuture(null);
        }
        String url = normalizeBase(taskForgeBaseUrl) + "/api/integrations/minecraft/player-status?uuid=" + p.getUniqueId();

        HttpRequest req = HttpRequest.newBuilder()
                .uri(URI.create(url))
                .timeout(Duration.ofSeconds(taskForgeTimeoutSeconds))
                .header("X-Minecraft-Key", taskForgeKey)
                .GET()
                .build();

        return httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
                .orTimeout(taskForgeTimeoutSeconds + 1L, TimeUnit.SECONDS)
                .thenApply(resp -> parseStatusResponse(resp))
                .exceptionally(ex -> {
                    getLogger().warning("TaskForge status call failed: " + ex.getMessage());
                    return null;
                });
    }

    void applyDebuffs(Player p, boolean debuffed) {
        if (!debuffsEnabled) return;

        int ticks = debuffDurationSeconds * 20;
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

    private static String normalizeBase(String base) {
        if (base.endsWith("/")) return base.substring(0, base.length() - 1);
        return base;
    }

    private PlayerStatusResponse parseStatusResponse(HttpResponse<String> resp) {
        if (resp == null) return null;
        if (resp.statusCode() < 200 || resp.statusCode() >= 300) {
            return null;
        }
        try {
            return GSON.fromJson(resp.body(), PlayerStatusResponse.class);
        } catch (Exception e) {
            return null;
        }
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
        boolean debuffed;
        boolean chargedThisWeek;
        int weeksUsed;
        int score;
        int weeklyPenalty;
    }

    private static final class TfListener implements Listener {
        private final TaskForgeLinkPlugin plugin;
        TfListener(TaskForgeLinkPlugin plugin) { this.plugin = plugin; }

        @EventHandler
        public void onJoin(PlayerJoinEvent e) {
            Player p = e.getPlayer();
            // Если TaskForge не настроен — просто ничего не делаем
            if (!plugin.canCallTaskForge()) return;

            plugin.notifyJoinAsync(p).thenAcceptAsync(st -> {
                if (st == null) return;
                plugin.applyDebuffs(p, st.debuffed);
                if (st.chargedThisWeek) {
                    p.getScheduler().run(plugin, t -> p.sendMessage("\u00A7eTaskForge: на этой неделе вход засчитан (-" + st.weeklyPenalty + ")"), null);
                }
            }, plugin.tfExecutor);
        }

        @EventHandler
        public void onDeath(PlayerDeathEvent e) {
            Player p = e.getEntity();
            if (!plugin.canCallTaskForge()) return;

            plugin.getStatusAsync(p).thenAcceptAsync(st -> {
                if (st == null) return;
                plugin.applyDebuffs(p, st.debuffed);
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
                if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) {
                    writeJson(ex, 405, "{\"error\":\"method_not_allowed\"}");
                    return;
                }

                // IP allowlist (если задана)
                if (allowedIps != null && !allowedIps.isEmpty()) {
                    String remoteIp = ex.getRemoteAddress().getAddress().getHostAddress();
                    if (allowedIps.stream().noneMatch(ip -> Objects.equals(ip, remoteIp))) {
                        writeJson(ex, 403, "{\"error\":\"forbidden\"}");
                        return;
                    }
                }

                Headers h = ex.getRequestHeaders();

                String gotKey = Optional.ofNullable(h.getFirst("X-TaskForge-Key")).orElse("");
                if (sharedKey.isBlank() || !sharedKey.equals(gotKey)) {
                    writeJson(ex, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }

                String requestId = Optional.ofNullable(h.getFirst("X-Request-Id")).orElse("");
                if (!requestId.isBlank()) {
                    boolean first = plugin.markRequestIdOnce(requestId);
                    if (!first) {
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
                writeJson(ex, 200, "{\"delivered\":true,\"online\":true,\"uuid\":\"" + uuid + "\"}");
            } catch (Exception e) {
                plugin.getLogger().severe("HTTP handler error: " + e.getMessage());
                writeJson(ex, 500, "{\"error\":\"server_error\"}");
            }
        }

        private static void writeJson(HttpExchange ex, int status, String json) throws IOException {
            byte[] bytes = json.getBytes(StandardCharsets.UTF_8);
            ex.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
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
