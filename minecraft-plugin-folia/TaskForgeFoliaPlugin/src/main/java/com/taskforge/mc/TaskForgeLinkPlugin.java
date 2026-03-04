package com.taskforge.mc;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.sun.net.httpserver.Headers;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;
import com.sun.net.httpserver.HttpServer;
import org.bukkit.Bukkit;
import org.bukkit.ChatColor;
import org.bukkit.command.Command;
import org.bukkit.command.CommandSender;
import org.bukkit.configuration.file.FileConfiguration;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.PlayerDeathEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Folia-supported plugin:
 * - Exposes HTTP endpoint for TaskForge to deliver a one-time linking code to an online player.
 * - Optionally pulls TaskForge "debuffed" status on join/death and applies effects.
 */
public final class TaskForgeLinkPlugin extends JavaPlugin implements Listener {

    private static final Gson GSON = new Gson();

    private HttpServer server;
    private final Map<String, Long> handledRequestIds = new ConcurrentHashMap<>();

    private String httpHost;
    private int httpPort;
    private String httpPath;

    private String taskForgeKey;
    private Set<String> allowedIps;

    private String taskforgeApiBaseUrl;
    private String minecraftKey;

    private int effectDurationTicks;
    private int slownessAmp;
    private int blindnessAmp;
    private int miningFatigueAmp;

    private final HttpClient httpClient = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(3))
            .build();

    @Override
    public void onEnable() {
        saveDefaultConfig();
        reloadLocalConfig();

        Bukkit.getPluginManager().registerEvents(this, this);

        try {
            startHttpServer();
            getLogger().info("TaskForgeLink HTTP server started at " + httpHost + ":" + httpPort + httpPath);
        } catch (Exception e) {
            getLogger().severe("Failed to start HTTP server: " + e.getMessage());
            e.printStackTrace();
        }

        // Cleanup requestIds cache every 10 minutes
        Bukkit.getGlobalRegionScheduler().runAtFixedRate(this, task -> cleanupRequestCache(), 20L * 60L, 20L * 60L * 10L);
    }

    @Override
    public void onDisable() {
        if (server != null) {
            server.stop(0);
            server = null;
        }
    }

    private void reloadLocalConfig() {
        FileConfiguration c = getConfig();
        httpHost = c.getString("http.host", "0.0.0.0");
        httpPort = c.getInt("http.port", 8123);
        httpPath = c.getString("http.path", "/taskforge/link/send");

        taskForgeKey = c.getString("security.taskForgeKey", "CHANGE_ME");
        allowedIps = new HashSet<>(c.getStringList("security.allowedIps"));

        taskforgeApiBaseUrl = c.getString("taskforge.apiBaseUrl", "").trim();
        minecraftKey = c.getString("taskforge.minecraftKey", "CHANGE_ME");

        effectDurationTicks = c.getInt("effects.durationTicks", 20);
        slownessAmp = c.getInt("effects.slownessAmplifier", 1);
        blindnessAmp = c.getInt("effects.blindnessAmplifier", 0);
        miningFatigueAmp = c.getInt("effects.miningFatigueAmplifier", 1);
    }

    private void startHttpServer() throws IOException {
        server = HttpServer.create(new InetSocketAddress(httpHost, httpPort), 0);
        server.createContext(httpPath, new LinkSendHandler());
        server.setExecutor(java.util.concurrent.Executors.newCachedThreadPool());
        server.start();
    }

    private class LinkSendHandler implements HttpHandler {
        @Override
        public void handle(HttpExchange exchange) throws IOException {
            try {
                if (!"POST".equalsIgnoreCase(exchange.getRequestMethod())) {
                    writeJson(exchange, 405, jsonError("method_not_allowed"));
                    return;
                }

                String remoteIp = exchange.getRemoteAddress().getAddress().getHostAddress();
                if (!allowedIps.isEmpty() && !allowedIps.contains(remoteIp)) {
                    writeJson(exchange, 403, jsonError("ip_not_allowed"));
                    return;
                }

                Headers headers = exchange.getRequestHeaders();
                String key = headers.getFirst("X-TaskForge-Key");
                if (key == null || !key.equals(taskForgeKey)) {
                    writeJson(exchange, 401, jsonError("unauthorized"));
                    return;
                }

                String requestId = headers.getFirst("X-Request-Id");
                if (requestId == null || requestId.isBlank()) {
                    writeJson(exchange, 400, jsonError("missing_request_id"));
                    return;
                }

                // idempotency
                if (handledRequestIds.containsKey(requestId)) {
                    JsonObject ok = new JsonObject();
                    ok.addProperty("delivered", true);
                    ok.addProperty("duplicate", true);
                    writeJson(exchange, 409, ok);
                    return;
                }

                String body = readBody(exchange);
                JsonObject payload = GSON.fromJson(body, JsonObject.class);
                if (payload == null || !payload.has("nick") || !payload.has("code")) {
                    writeJson(exchange, 400, jsonError("bad_payload"));
                    return;
                }

                String nick = payload.get("nick").getAsString();
                String code = payload.get("code").getAsString();

                Player player = Bukkit.getPlayerExact(nick);
                if (player == null || !player.isOnline()) {
                    JsonObject resp = new JsonObject();
                    resp.addProperty("delivered", false);
                    resp.addProperty("online", false);
                    resp.addProperty("reason", "offline");
                    writeJson(exchange, 404, resp);
                    return;
                }

                handledRequestIds.put(requestId, System.currentTimeMillis());

                // Folia-safe send message
                player.getScheduler().run(TaskForgeLinkPlugin.this, task -> {
                    player.sendMessage(ChatColor.AQUA + "\u2705 Код привязки TaskForge: " + ChatColor.YELLOW + code);
                    player.sendMessage(ChatColor.GRAY + "Введи этот код на сайте в профиле. Код действует ограниченное время.");
                }, null);

                JsonObject resp = new JsonObject();
                resp.addProperty("delivered", true);
                resp.addProperty("online", true);
                resp.addProperty("uuid", player.getUniqueId().toString());
                writeJson(exchange, 200, resp);

            } catch (Exception ex) {
                getLogger().warning("HTTP handler error: " + ex.getMessage());
                ex.printStackTrace();
                writeJson(exchange, 500, jsonError("internal_error"));
            }
        }
    }

    private void cleanupRequestCache() {
        long now = System.currentTimeMillis();
        long maxAgeMs = 30L * 60L * 1000L; // 30 minutes
        handledRequestIds.entrySet().removeIf(e -> (now - e.getValue()) > maxAgeMs);
    }

    private static String readBody(HttpExchange ex) throws IOException {
        try (InputStream is = ex.getRequestBody()) {
            return new String(is.readAllBytes(), StandardCharsets.UTF_8);
        }
    }

    private static void writeJson(HttpExchange ex, int status, JsonObject obj) throws IOException {
        byte[] data = GSON.toJson(obj).getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
        ex.sendResponseHeaders(status, data.length);
        try (OutputStream os = ex.getResponseBody()) {
            os.write(data);
        }
    }

    private static JsonObject jsonError(String code) {
        JsonObject o = new JsonObject();
        o.addProperty("error", code);
        return o;
    }

    // ----- Debuff pull logic (optional) -----

    @EventHandler
    public void onJoin(PlayerJoinEvent e) {
        Player p = e.getPlayer();
        // Delay a bit to avoid join spam
        p.getScheduler().runDelayed(this, task -> refreshDebuffStatus(p, true), null, 20L);
    }

    @EventHandler
    public void onDeath(PlayerDeathEvent e) {
        Player p = e.getEntity();
        // After death, re-check and re-apply if needed
        p.getScheduler().runDelayed(this, task -> refreshDebuffStatus(p, false), null, 20L);
    }

    private void refreshDebuffStatus(Player p, boolean countJoin) {
        if (taskforgeApiBaseUrl == null || taskforgeApiBaseUrl.isEmpty()) {
            return; // disabled
        }

        String endpoint = countJoin ? "/api/integrations/minecraft/events/join" : "/api/integrations/minecraft/player-status";

        JsonObject body = new JsonObject();
        body.addProperty("nick", p.getName());
        body.addProperty("uuid", p.getUniqueId().toString());

        HttpRequest.Builder b = HttpRequest.newBuilder()
                .timeout(Duration.ofSeconds(5))
                .header("Content-Type", "application/json")
                .header("X-Minecraft-Key", minecraftKey);

        URI uri = URI.create(trimEndSlash(taskforgeApiBaseUrl) + endpoint);

        HttpRequest req;
        if (countJoin) {
            req = b.POST(HttpRequest.BodyPublishers.ofString(GSON.toJson(body))).uri(uri).build();
        } else {
            // GET with query params
            URI u2 = URI.create(trimEndSlash(taskforgeApiBaseUrl) + endpoint + "?nick=" + urlEncode(p.getName()) + "&uuid=" + urlEncode(p.getUniqueId().toString()));
            req = b.GET().uri(u2).build();
        }

        // Async HTTP call, then schedule effects on player's region thread
        httpClient.sendAsync(req, HttpResponse.BodyHandlers.ofString())
                .thenAccept(resp -> {
                    try {
                        if (resp.statusCode() >= 200 && resp.statusCode() < 300) {
                            JsonObject r = GSON.fromJson(resp.body(), JsonObject.class);
                            boolean debuffed = r != null && r.has("debuffed") && r.get("debuffed").getAsBoolean();

                            p.getScheduler().run(this, task -> {
                                if (!p.isOnline()) return;
                                if (debuffed) applyDebuffs(p);
                                else clearDebuffs(p);
                            }, null);
                        }
                    } catch (Exception ignored) {
                        // keep silent
                    }
                })
                .exceptionally(ex -> null);
    }

    private void applyDebuffs(Player p) {
        // keep refreshing short duration effects
        p.addPotionEffect(new PotionEffect(PotionEffectType.SLOW, effectDurationTicks, slownessAmp, true, false, true));
        p.addPotionEffect(new PotionEffect(PotionEffectType.BLINDNESS, effectDurationTicks, blindnessAmp, true, false, true));
        p.addPotionEffect(new PotionEffect(PotionEffectType.SLOW_DIGGING, effectDurationTicks, miningFatigueAmp, true, false, true));
    }

    private void clearDebuffs(Player p) {
        p.removePotionEffect(PotionEffectType.SLOW);
        p.removePotionEffect(PotionEffectType.BLINDNESS);
        p.removePotionEffect(PotionEffectType.SLOW_DIGGING);
    }

    private static String trimEndSlash(String s) {
        if (s.endsWith("/")) return s.substring(0, s.length() - 1);
        return s;
    }

    private static String urlEncode(String s) {
        return java.net.URLEncoder.encode(s, StandardCharsets.UTF_8);
    }

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        if (command.getName().equalsIgnoreCase("tflink")) {
            sender.sendMessage(ChatColor.AQUA + "TaskForgeLink plugin is running.");
            sender.sendMessage(ChatColor.GRAY + "Этот плагин принимает коды от TaskForge и может применять дебафы по статусу.");
            return true;
        }
        return false;
    }
}
