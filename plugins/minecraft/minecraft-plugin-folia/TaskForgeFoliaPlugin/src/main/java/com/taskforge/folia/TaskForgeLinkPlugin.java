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
import org.bukkit.configuration.InvalidConfigurationException;
import org.bukkit.configuration.file.YamlConfiguration;
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
import java.io.File;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.URI;
import java.net.URLEncoder;
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
import java.util.concurrent.atomic.AtomicBoolean;
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
    private final ConcurrentHashMap<UUID, LinkStateSnapshot> linkStates = new ConcurrentHashMap<>();
    private final Set<UUID> linkRefreshInFlight = ConcurrentHashMap.newKeySet();
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
    private int debugConnectivityProbeSeconds;
    private final AtomicLong httpSequence = new AtomicLong();
    private final AtomicBoolean reloadInProgress = new AtomicBoolean(false);
    private final Object runtimeLock = new Object();
    private volatile boolean runtimeStarted;
    private volatile boolean runtimeHttpStarted;
    private volatile String runtimeHttpHost = "<not-started>";
    private volatile int runtimeHttpPort = -1;
    private volatile String runtimeHttpPath = "<not-started>";

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

        // Older debug builds shipped noisy legacy keys. Remove them from existing configs;
        // focused death-flow logging remains enabled through debug.deathRecovery.
        for (String legacyPath : List.of(
                "debug.http",
                "debug.httpBodies",
                "debug.scheduler",
                "debug.journal",
                "debug.heartbeat",
                "debug.connectivityProbeSeconds",
                "deathRecovery.linkStatusRefreshSeconds")) {
            if (getConfig().contains(legacyPath)) {
                getConfig().set(legacyPath, null);
                changed = true;
            }
        }

        if (changed) {
            saveConfig();
            getLogger().info("Config keys/defaults were normalized; noisy legacy diagnostics and polling probes are disabled.");
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

        for (Player online : Bukkit.getOnlinePlayers()) {
            trackOnline(online);
        }
        Bukkit.getPluginManager().registerEvents(new TfListener(this), this);

        synchronized (runtimeLock) {
            reloadConfig();
            migrateConfigKeys();
            if (!startReloadableRuntime("plugin-enable")) {
                getLogger().severe("[TaskForgeLink] Runtime started in degraded mode. Fix config.yml and run /tflink reload.");
            }
        }
        getLogger().info("[TaskForgeLink] enabled; trackedOnlinePlayers=" + onlinePlayerCount());
    }

    private RuntimeSettings loadRuntimeConfiguration() {
        debugEnabled = getConfig().getBoolean("debug.enabled", true);
        debugHttp = getConfig().getBoolean("debug.verboseHttp", false);
        debugHttpBodies = getConfig().getBoolean("debug.verboseHttpBodies", false);
        debugDeathRecovery = getConfig().getBoolean("debug.deathRecovery", true);
        debugScheduler = getConfig().getBoolean("debug.verboseScheduler", false);
        debugJournal = getConfig().getBoolean("debug.verboseJournal", false);
        debugConnectivityProbeSeconds = Math.max(0, getConfig().getInt("diagnostics.connectivityProbeSeconds", 0));

        String host = getConfig().getString("http.host", "0.0.0.0");
        int port = getConfig().getInt("http.port", 25566);
        String path = getConfig().getString("http.path", "/taskforge/link/send");
        if (isBlank(path) || !path.startsWith("/")) {
            throw new IllegalArgumentException("http.path must start with '/'");
        }
        if (port < 1 || port > 65535) {
            throw new IllegalArgumentException("http.port must be between 1 and 65535");
        }

        String webhookKey = firstNonBlank(
                getConfig().getString("security.taskforgeKey", ""),
                getConfig().getString("security.taskforgekey", "")
        );
        List<String> allowedIps = List.copyOf(getConfig().getStringList("security.allowedIps"));

        taskForgeBaseUrl = firstNonBlank(
                Optional.ofNullable(getConfig().getString("taskforge.apiBaseUrl")).orElse("").trim(),
                Optional.ofNullable(getConfig().getString("taskforge.apibaseUrl")).orElse("").trim()
        );
        taskForgeKey = firstNonBlank(
                Optional.ofNullable(getConfig().getString("taskforge.pluginKey")).orElse("").trim(),
                Optional.ofNullable(getConfig().getString("taskforge.pluginkey")).orElse("").trim()
        );
        taskForgeTimeoutSeconds = Math.max(1, getConfig().getInt("taskforge.timeoutSeconds", 5));
        chatEnabled = getConfig().getBoolean("chat.enabled", true);
        chatPollIntervalSeconds = Math.max(5, getConfig().getInt("chat.pollIntervalSeconds", 12));
        chatPollOnlyWhenPlayersOnline = getConfig().getBoolean("chat.pollOnlyWhenPlayersOnline", true);
        chatForwardJoinQuit = getConfig().getBoolean("chat.forwardJoinQuit", true);
        chatForwardPlayerMessages = getConfig().getBoolean("chat.forwardPlayerMessages", true);
        chatForwardAdvancements = getConfig().getBoolean("chat.forwardAdvancements", true);
        chatIncludeRecipeAdvancements = getConfig().getBoolean("chat.includeRecipeAdvancements", false);
        chatIncludeRootAdvancements = getConfig().getBoolean("chat.includeRootAdvancements", false);
        chatSitePrefix = getConfig().getString("chat.sitePrefix", "§d[TaskForge]§r ");
        if (Instant.EPOCH.equals(chatCursorUtc)) chatCursorUtc = Instant.now();
        deathCoordinatesCost = Math.max(1, getConfig().getInt("deathRecovery.coordinatesCost", 10));
        deathChestCost = Math.max(1, getConfig().getInt("deathRecovery.chestCost", 50));
        deathTeleportCost = Math.max(1, getConfig().getInt("deathRecovery.teleportCost", 100));

        runtimeHttpHost = host;
        runtimeHttpPort = port;
        runtimeHttpPath = path;

        getLogger().info("[TaskForgeLink][reload] config loaded diagnostics=" + debugEnabled
                + " deathRecovery=" + debugDeathRecovery
                + " verboseHttp=" + debugHttp + " verboseHttpBodies=" + debugHttpBodies
                + " verboseScheduler=" + debugScheduler + " verboseJournal=" + debugJournal
                + " connectivityProbeSeconds=" + debugConnectivityProbeSeconds);
        getLogger().info("[TaskForgeLink][reload] HTTP " + host + ":" + port + " path=" + path
                + " allowedIps=" + allowedIps.size() + " webhookKey=" + keyFingerprint(webhookKey)
                + " len=" + webhookKey.length());
        getLogger().info("[TaskForgeLink][reload] backend baseUrl=" + (taskForgeBaseUrl.isBlank() ? "<missing>" : taskForgeBaseUrl)
                + " pluginKey=" + keyFingerprint(taskForgeKey) + " len=" + taskForgeKey.length()
                + " timeoutSeconds=" + taskForgeTimeoutSeconds);
        getLogger().info("[TaskForgeLink][reload] death costs coordinates=" + deathCoordinatesCost
                + " chest=" + deathChestCost + " teleport=" + deathTeleportCost
                + " linkStatusRefresh=event-driven");

        if (!canCallTaskForge()) {
            getLogger().warning("[TaskForgeLink] Minecraft -> TaskForge calls are disabled because apiBaseUrl or pluginKey is empty.");
        }
        if ("CHANGE_ME".equals(webhookKey) || "CHANGE_ME".equals(taskForgeKey)) {
            getLogger().severe("[TaskForgeLink] CHANGE_ME is still present in a security key. Replace it before exposing the server.");
        }

        exemptNicksLower.clear();
        exemptUuidsLower.clear();
        for (String n : getConfig().getStringList("exemptions.nicks")) {
            if (n != null && !n.trim().isEmpty()) exemptNicksLower.add(n.trim().toLowerCase(Locale.ROOT));
        }
        for (String u : getConfig().getStringList("exemptions.uuids")) {
            if (u != null && !u.trim().isEmpty()) exemptUuidsLower.add(u.trim().toLowerCase(Locale.ROOT));
        }
        getLogger().info("[TaskForgeLink][reload] exemptions nicks=" + exemptNicksLower.size()
                + " uuids=" + exemptUuidsLower.size());

        return new RuntimeSettings(host, port, path, webhookKey, allowedIps);
    }

    private boolean startReloadableRuntime(String reason) {
        RuntimeSettings settings;
        try {
            settings = loadRuntimeConfiguration();
        } catch (Exception ex) {
            getLogger().log(java.util.logging.Level.SEVERE,
                    "[TaskForgeLink][reload] Cannot load runtime configuration reason=" + reason, ex);
            runtimeStarted = false;
            return false;
        }

        boolean httpStarted = false;
        try {
            httpClient = buildHttpClient();
            if (tfExecutor == null || tfExecutor.isShutdown()) {
                tfExecutor = Executors.newFixedThreadPool(2, r -> {
                    Thread t = new Thread(r, "taskforge-link-http");
                    t.setDaemon(true);
                    return t;
                });
            }

            try {
                InetAddress addr = InetAddress.getByName(settings.host());
                server = HttpServer.create(new InetSocketAddress(addr, settings.port()), 0);
                server.createContext(settings.path(), new SendCodeHandler(this, settings.webhookKey(), settings.allowedIps()));
                server.createContext("/health", ex -> {
                    int onlinePlayerCount = onlinePlayerCount();
                    debug("http-in", "health request ip=" + ex.getRemoteAddress() + " method=" + ex.getRequestMethod()
                            + " onlinePlayers=" + onlinePlayerCount + " runtimeStarted=" + runtimeStarted);
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
                httpStarted = true;
                runtimeHttpStarted = true;
                getLogger().info("[TaskForgeLink][reload] HTTP server started reason=" + reason + " address="
                        + settings.host() + ":" + settings.port() + " path=" + settings.path());
            } catch (Exception ex) {
                getLogger().log(java.util.logging.Level.SEVERE,
                        "[TaskForgeLink][reload] Failed to start HTTP server reason=" + reason
                                + " address=" + settings.host() + ":" + settings.port(), ex);
                stopHttpServerOnly();
            }

            for (Player online : onlinePlayersSnapshot()) {
                linkStates.put(online.getUniqueId(), new LinkStateSnapshot(
                        LinkState.UNKNOWN, Instant.EPOCH, Instant.now(), "runtime-" + reason, "awaiting-refresh"));
            }

            deathRecoveryManager = new DeathRecoveryManager(this);
            deathRecoveryManager.enable();

            for (Player online : onlinePlayersSnapshot()) {
                refreshPlayerLinkState(online, "runtime-" + reason);
            }

            janitor = Executors.newSingleThreadScheduledExecutor(r -> {
                Thread t = new Thread(r, "taskforge-link-janitor");
                t.setDaemon(true);
                return t;
            });
            janitor.scheduleAtFixedRate(this::cleanupRequestCache, 5, 5, TimeUnit.MINUTES);
            if (canCallTaskForge() && debugConnectivityProbeSeconds > 0) {
                janitor.scheduleAtFixedRate(this::probeTaskForgeConnectivitySafe,
                        debugConnectivityProbeSeconds, debugConnectivityProbeSeconds, TimeUnit.SECONDS);
            }
            if (chatEnabled && canCallTaskForge()) {
                janitor.scheduleAtFixedRate(this::pollSiteChatSafe, chatPollIntervalSeconds, chatPollIntervalSeconds, TimeUnit.SECONDS);
            }

            runtimeStarted = true;
            getLogger().info("[TaskForgeLink][reload] runtime started reason=" + reason
                    + " httpStarted=" + httpStarted + " trackedOnlinePlayers=" + onlinePlayerCount()
                    + " connectivityProbeSeconds=" + debugConnectivityProbeSeconds
                    + " linkStatusRefresh=join/reload/backend-events-only"
                    + " chatEnabled=" + chatEnabled + " deathRecoveryEnabled="
                    + getConfig().getBoolean("deathRecovery.enabled", true));
            return httpStarted;
        } catch (Throwable error) {
            getLogger().log(java.util.logging.Level.SEVERE,
                    "[TaskForgeLink][reload] runtime startup failed; rolling back partial runtime reason=" + reason, error);
            stopReloadableRuntime("startup-rollback");
            return false;
        }
    }

    private void stopHttpServerOnly() {
        runtimeHttpStarted = false;
        if (server != null) {
            try {
                server.stop(0);
            } catch (Exception ex) {
                getLogger().log(java.util.logging.Level.WARNING, "[TaskForgeLink][reload] HTTP stop failed", ex);
            }
            server = null;
        }
        if (serverExecutor != null) {
            serverExecutor.shutdownNow();
            serverExecutor = null;
        }
    }

    private void stopReloadableRuntime(String reason) {
        runtimeStarted = false;
        getLogger().info("[TaskForgeLink][reload] stopping runtime reason=" + reason
                + " onlinePlayers=" + onlinePlayerCount() + " inFlightLinkRefreshes=" + linkRefreshInFlight.size());

        if (deathRecoveryManager != null) {
            try {
                deathRecoveryManager.disable();
            } catch (Exception ex) {
                getLogger().log(java.util.logging.Level.WARNING,
                        "[TaskForgeLink][reload] death recovery shutdown failed", ex);
            }
            deathRecoveryManager = null;
        }
        stopHttpServerOnly();
        if (janitor != null) {
            janitor.shutdownNow();
            janitor = null;
        }
        if ("plugin-disable".equals(reason) && tfExecutor != null) {
            tfExecutor.shutdownNow();
            tfExecutor = null;
        }
        linkRefreshInFlight.clear();
        getLogger().info("[TaskForgeLink][reload] runtime stopped reason=" + reason);
    }

    private void validateConfigFileBeforeReload() throws IOException, InvalidConfigurationException {
        File file = new File(getDataFolder(), "config.yml");
        YamlConfiguration candidate = new YamlConfiguration();
        candidate.load(file);

        String path = candidate.getString("http.path", "/taskforge/link/send");
        int port = candidate.getInt("http.port", 25566);
        if (isBlank(path) || !path.startsWith("/")) {
            throw new InvalidConfigurationException("http.path must start with '/'");
        }
        if (port < 1 || port > 65535) {
            throw new InvalidConfigurationException("http.port must be between 1 and 65535");
        }
        String baseUrl = firstNonBlank(
                candidate.getString("taskforge.apiBaseUrl", ""),
                candidate.getString("taskforge.apibaseUrl", ""));
        if (!isBlank(baseUrl)) {
            URI parsed = URI.create(baseUrl);
            if (parsed.getScheme() == null || parsed.getHost() == null) {
                throw new InvalidConfigurationException("taskforge.apiBaseUrl must be an absolute http/https URL");
            }
            String scheme = parsed.getScheme().toLowerCase(Locale.ROOT);
            if (!"http".equals(scheme) && !"https".equals(scheme)) {
                throw new InvalidConfigurationException("taskforge.apiBaseUrl must use http or https");
            }
        }
        getLogger().info("[TaskForgeLink][reload] config preflight passed file=" + file.getAbsolutePath()
                + " http=" + candidate.getString("http.host", "0.0.0.0") + ":" + port + path);
    }

    private void requestRuntimeReload(CommandSender sender) {
        if (!reloadInProgress.compareAndSet(false, true)) {
            sendCommandMessage(sender, "§eTaskForgeLink: перезагрузка уже выполняется.");
            return;
        }
        sendCommandMessage(sender, "§eTaskForgeLink: перечитываю config.yml и перезапускаю HTTP, чат, кеш привязок и восстановление смертей...");
        getLogger().info("[TaskForgeLink][reload] requested by=" + sender.getName());

        Bukkit.getGlobalRegionScheduler().execute(this, () -> {
            boolean ok = false;
            try {
                validateConfigFileBeforeReload();
                synchronized (runtimeLock) {
                    stopReloadableRuntime("command-reload");
                    reloadConfig();
                    migrateConfigKeys();
                    ok = startReloadableRuntime("command-reload");
                }
                if (ok) {
                    sendCommandMessage(sender, "§aTaskForgeLink: конфигурация полностью применена без перезапуска сервера.");
                } else {
                    sendCommandMessage(sender, "§cTaskForgeLink: конфиг перечитан, но HTTP-сервер не поднялся. Смотри подробный stack trace в консоли и повтори /tflink reload.");
                }
            } catch (Throwable error) {
                getLogger().log(java.util.logging.Level.SEVERE, "[TaskForgeLink][reload] reload failed", error);
                sendCommandMessage(sender, "§cTaskForgeLink: перезагрузка завершилась ошибкой: "
                        + error.getClass().getSimpleName() + ": " + String.valueOf(error.getMessage()));
            } finally {
                reloadInProgress.set(false);
            }
        });
    }

    private void sendCommandMessage(CommandSender sender, String message) {
        if (sender instanceof Player player) {
            player.getScheduler().run(this, task -> {
                if (player.isOnline()) player.sendMessage(message);
            }, () -> getLogger().info("[TaskForgeLink][reload] command sender retired before message delivery uuid="
                    + player.getUniqueId()));
            return;
        }
        sender.sendMessage(message);
    }

    private Executor callbackExecutor() {
        ExecutorService current = tfExecutor;
        if (current != null && !current.isShutdown()) return current;
        return Runnable::run;
    }

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        String name = command.getName().toLowerCase(Locale.ROOT);
        if ("tfback".equals(name)) {
            sender.sendMessage("TaskForge: после смерти используй кнопки [Координаты], [Сундук], [Вернуться], [Сундук + возврат] или [Обычный дроп].");
            return true;
        }
        if (!"taskforgelink".equals(name)) return false;

        if (args.length == 1 && ("reload".equalsIgnoreCase(args[0]) || "refresh".equalsIgnoreCase(args[0]))) {
            if (!sender.hasPermission("taskforge.link.reload")) {
                sender.sendMessage("§cНет права taskforge.link.reload.");
                return true;
            }
            requestRuntimeReload(sender);
            return true;
        }
        if (args.length == 1 && "status".equalsIgnoreCase(args[0])) {
            if (!sender.hasPermission("taskforge.link.reload")) {
                sender.sendMessage("§cНет права taskforge.link.reload.");
                return true;
            }
            sender.sendMessage("§eTaskForgeLink " + getDescription().getVersion()
                    + "§7: runtime=" + (runtimeStarted ? "§aON" : "§cOFF")
                    + "§7, HTTP=" + (runtimeHttpStarted ? "§aON" : "§cOFF")
                    + "§7(§f" + runtimeHttpHost + ":" + runtimeHttpPort + runtimeHttpPath + "§7)"
                    + "§7, backend=" + (canCallTaskForge() ? "§aON" : "§cOFF")
                    + "§7, online=§f" + onlinePlayerCount());
            return true;
        }
        sender.sendMessage("§eИспользование: /" + label + " <reload|refresh|status>");
        return true;
    }

    @Override
    public List<String> onTabComplete(CommandSender sender, Command command, String alias, String[] args) {
        if (!"taskforgelink".equalsIgnoreCase(command.getName()) || args.length != 1) return List.of();
        String prefix = args[0].toLowerCase(Locale.ROOT);
        return List.of("reload", "refresh", "status").stream().filter(value -> value.startsWith(prefix)).toList();
    }

    @Override
    public void onDisable() {
        synchronized (runtimeLock) {
            stopReloadableRuntime("plugin-disable");
        }
        seenRequestIds.clear();
        onlinePlayers.clear();
        onlinePlayersByName.clear();
        linkStates.clear();
        linkRefreshInFlight.clear();
        getLogger().info("[TaskForgeLink] disabled");
    }

    private record RuntimeSettings(String host, int port, String path, String webhookKey, List<String> allowedIps) {
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
            recordLinkStateFailure(p == null ? null : p.getUniqueId(), "join", "backend-not-configured");
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
                        recordLinkStateFailure(p.getUniqueId(), "join", unwrap(error).getClass().getSimpleName());
                        return null;
                    }
                    logHttpResponse(httpId, "join", resp);
                    if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
                    else markTaskForgeFailure("join-http", null);
                    PlayerStatusResponse status = parseStatusResponse(resp);
                    recordLinkStateResponse(p.getUniqueId(), status, "join", resp == null ? "null-response" : "http-" + resp.statusCode());
                    return status;
                });
    }

    CompletableFuture<PlayerStatusResponse> getStatusAsync(Player p) {
        if (!canCallTaskForge()) {
            recordLinkStateFailure(p == null ? null : p.getUniqueId(), "status", "backend-not-configured");
            return CompletableFuture.completedFuture(null);
        }
        String url = normalizeBase(taskForgeBaseUrl)
                + "/api/integrations/minecraft/player-status?uuid="
                + encodeQuery(p.getUniqueId().toString())
                + "&nick=" + encodeQuery(p.getName());
        debug("link-cache", "status identity query player=" + p.getName() + "/" + p.getUniqueId()
                + " includesUuid=true includesNick=true");

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
                        recordLinkStateFailure(p.getUniqueId(), "status", unwrap(error).getClass().getSimpleName());
                        return null;
                    }
                    logHttpResponse(httpId, "status", resp);
                    if (resp != null && resp.statusCode() >= 200 && resp.statusCode() < 300) markTaskForgeSuccess();
                    else markTaskForgeFailure("status-http", null);
                    PlayerStatusResponse status = parseStatusResponse(resp);
                    recordLinkStateResponse(p.getUniqueId(), status, "status", resp == null ? "null-response" : "http-" + resp.statusCode());
                    return status;
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
        String preview = message.replace('\n', ' ').replace('\r', ' ');
        if (preview.length() > 240) preview = preview.substring(0, 240) + "...";
        debugScheduler("queue chat player=" + p.getName() + " uuid=" + p.getUniqueId()
                + " dead=" + p.isDead() + " online=" + p.isOnline() + " len=" + message.length()
                + " message=" + preview);
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
        linkStates.put(id, new LinkStateSnapshot(LinkState.UNKNOWN, Instant.EPOCH, Instant.now(), "track-online", "awaiting-status"));
        debug("player", "track online name=" + player.getName() + " uuid=" + id + " dead=" + player.isDead()
                + " linkState=UNKNOWN");
        String name = player.getName();
        if (name != null && !name.isBlank()) {
            onlinePlayersByName.put(name.toLowerCase(Locale.ROOT), id);
        }
    }

    void trackOffline(Player player) {
        if (player == null) return;
        UUID id = player.getUniqueId();
        onlinePlayers.remove(id, player);
        linkStates.remove(id);
        linkRefreshInFlight.remove(id);
        debug("player", "track offline name=" + player.getName() + " uuid=" + id + " linkCacheRemoved=true");
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


    enum LinkState {
        LINKED,
        UNLINKED,
        UNKNOWN
    }

    static final class LinkStateSnapshot {
        final LinkState state;
        final Instant confirmedAt;
        final Instant lastAttemptAt;
        final String source;
        final String detail;

        LinkStateSnapshot(LinkState state, Instant confirmedAt, Instant lastAttemptAt, String source, String detail) {
            this.state = state == null ? LinkState.UNKNOWN : state;
            this.confirmedAt = confirmedAt == null ? Instant.EPOCH : confirmedAt;
            this.lastAttemptAt = lastAttemptAt == null ? Instant.EPOCH : lastAttemptAt;
            this.source = source == null ? "unknown" : source;
            this.detail = detail == null ? "" : detail;
        }
    }

    LinkState linkState(UUID playerId) {
        if (playerId == null) return LinkState.UNKNOWN;
        LinkStateSnapshot snapshot = linkStates.get(playerId);
        return snapshot == null ? LinkState.UNKNOWN : snapshot.state;
    }

    String linkStateDebug(UUID playerId) {
        LinkStateSnapshot snapshot = playerId == null ? null : linkStates.get(playerId);
        if (snapshot == null) return "state=UNKNOWN cache=missing";
        long confirmedAge = snapshot.confirmedAt.equals(Instant.EPOCH)
                ? -1L : Math.max(0L, Duration.between(snapshot.confirmedAt, Instant.now()).toSeconds());
        long attemptAge = snapshot.lastAttemptAt.equals(Instant.EPOCH)
                ? -1L : Math.max(0L, Duration.between(snapshot.lastAttemptAt, Instant.now()).toSeconds());
        return "state=" + snapshot.state + " source=" + snapshot.source + " detail=" + snapshot.detail
                + " confirmedAgeSec=" + confirmedAge + " attemptAgeSec=" + attemptAge;
    }

    String linkStateCounts() {
        int linked = 0, unlinked = 0, unknown = 0;
        for (LinkStateSnapshot snapshot : linkStates.values()) {
            switch (snapshot.state) {
                case LINKED -> linked++;
                case UNLINKED -> unlinked++;
                default -> unknown++;
            }
        }
        return "linked=" + linked + " unlinked=" + unlinked + " unknown=" + unknown
                + " refreshInFlight=" + linkRefreshInFlight.size();
    }

    private void recordLinkStateResponse(UUID playerId, PlayerStatusResponse status, String source, String detail) {
        if (playerId == null) return;
        if (status == null) {
            recordLinkStateFailure(playerId, source, detail + ":parse-null");
            return;
        }
        LinkState newState = status.linked ? LinkState.LINKED : LinkState.UNLINKED;
        Instant now = Instant.now();
        LinkStateSnapshot previous = linkStates.put(playerId,
                new LinkStateSnapshot(newState, now, now, source, detail));
        debug("link-cache", "authoritative update player=" + playerId + " previous="
                + (previous == null ? "missing" : previous.state) + " current=" + newState
                + " source=" + source + " detail=" + detail + " balance=" + status.minecraftBalance);
        DeathRecoveryManager manager = deathRecoveryManager;
        if (manager != null && (previous == null || previous.state != newState)) {
            debug("link-cache", "state transition callback player=" + playerId
                    + " previous=" + (previous == null ? "missing" : previous.state)
                    + " current=" + newState + " source=" + source);
            manager.onLinkStateChanged(playerId, newState, source);
        } else if (manager != null) {
            debug("link-cache", "state unchanged; death callback suppressed player=" + playerId
                    + " state=" + newState + " source=" + source);
        }
    }

    void markLinkStateUnlinked(UUID playerId, String source) {
        if (playerId == null) return;
        Instant now = Instant.now();
        LinkStateSnapshot previous = linkStates.put(playerId,
                new LinkStateSnapshot(LinkState.UNLINKED, now, now, source, "backend-authoritative-not-linked"));
        debug("link-cache", "forced UNLINKED from backend player=" + playerId
                + " previous=" + (previous == null ? "missing" : previous.state) + " source=" + source);
        DeathRecoveryManager manager = deathRecoveryManager;
        if (manager != null && (previous == null || previous.state != LinkState.UNLINKED)) {
            manager.onLinkStateChanged(playerId, LinkState.UNLINKED, source);
        }
    }

    private void recordLinkStateFailure(UUID playerId, String source, String detail) {
        if (playerId == null) return;
        Instant now = Instant.now();
        linkStates.compute(playerId, (id, previous) -> {
            if (previous == null || previous.state == LinkState.UNKNOWN) {
                debug("link-cache", "status unavailable player=" + playerId
                        + " state=UNKNOWN source=" + source + " detail=" + detail);
                return new LinkStateSnapshot(LinkState.UNKNOWN, Instant.EPOCH, now, source, detail);
            }
            debug("link-cache", "status refresh failed player=" + playerId + " preserving=" + previous.state
                    + " source=" + source + " detail=" + detail
                    + " confirmedAt=" + previous.confirmedAt);
            return new LinkStateSnapshot(previous.state, previous.confirmedAt, now, source, detail + ":preserved");
        });
    }

    private void refreshPlayerLinkState(Player player, String source) {
        if (player == null || !player.isOnline() || isExempt(player)) return;
        UUID playerId = player.getUniqueId();
        if (!linkRefreshInFlight.add(playerId)) {
            debug("link-cache", "refresh skipped; already in flight player=" + playerId + " source=" + source);
            return;
        }
        debug("link-cache", "refresh start player=" + player.getName() + "/" + playerId
                + " source=" + source + " " + linkStateDebug(playerId));
        getStatusAsync(player).whenComplete((status, error) -> {
            linkRefreshInFlight.remove(playerId);
            if (error != null) recordLinkStateFailure(playerId, source, unwrap(error).getClass().getSimpleName());
            debug("link-cache", "refresh end player=" + player.getName() + "/" + playerId
                    + " source=" + source + " result=" + (status == null ? "null" : status.linked)
                    + " " + linkStateDebug(playerId));
        });
    }

    private static String encodeQuery(String value) {
        return URLEncoder.encode(value == null ? "" : value, StandardCharsets.UTF_8);
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

            plugin.debug("link-cache", "join status request player=" + p.getName() + "/" + p.getUniqueId()
                    + " " + plugin.linkStateDebug(p.getUniqueId()));
            plugin.notifyJoinAsync(p).thenAcceptAsync(st -> {
                if (st == null) {
                    plugin.sendChat(p, "§eTaskForge: §7не удалось проверить привязку. Пока проверка недоступна, смерти остаются полностью обычными.");
                    return;
                }
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
            }, plugin.callbackExecutor());
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
                String uuid = finalP.getUniqueId().toString();
                CompletableFuture<Boolean> deliveredFuture = new CompletableFuture<>();

                plugin.getLogger().info("[TF->MC] scheduling code delivery nick=" + finalP.getName()
                        + " uuid=" + uuid + " requestId=" + requestId + " ip=" + remoteIp);

                // Folia-safe: the HTTP request is acknowledged only after the entity task actually runs.
                finalP.getScheduler().run(plugin, task -> {
                    try {
                        finalP.sendMessage("\u00A7a\u2714 \u00A7fКод привязки TaskForge: \u00A7e" + code);
                        finalP.sendMessage("\u00A77Введи его на сайте в профиле. Код действует примерно 10 минут.");
                        deliveredFuture.complete(true);
                        plugin.getLogger().info("[TF->MC] entity task delivered code nick=" + finalP.getName()
                                + " uuid=" + uuid + " requestId=" + requestId);
                    } catch (Throwable sendError) {
                        deliveredFuture.completeExceptionally(sendError);
                        plugin.getLogger().log(java.util.logging.Level.SEVERE,
                                "[TF->MC] entity task failed while delivering code nick=" + finalP.getName()
                                        + " uuid=" + uuid + " requestId=" + requestId,
                                sendError);
                    }
                }, () -> {
                    deliveredFuture.complete(false);
                    plugin.getLogger().warning("[TF->MC] entity scheduler retired before code delivery nick="
                            + finalP.getName() + " uuid=" + uuid + " requestId=" + requestId);
                });

                final boolean delivered;
                try {
                    delivered = deliveredFuture.get(4, TimeUnit.SECONDS);
                } catch (TimeoutException timeout) {
                    plugin.getLogger().log(java.util.logging.Level.SEVERE,
                            "[TF->MC] timed out waiting for entity scheduler nick=" + finalP.getName()
                                    + " uuid=" + uuid + " requestId=" + requestId,
                            timeout);
                    writeJson(ex, 504, "{\"delivered\":false,\"online\":true,\"reason\":\"scheduler_timeout\"}");
                    return;
                } catch (ExecutionException execution) {
                    plugin.getLogger().log(java.util.logging.Level.SEVERE,
                            "[TF->MC] entity scheduler delivery failed nick=" + finalP.getName()
                                    + " uuid=" + uuid + " requestId=" + requestId,
                            execution.getCause() == null ? execution : execution.getCause());
                    writeJson(ex, 500, "{\"delivered\":false,\"online\":true,\"reason\":\"scheduler_failed\"}");
                    return;
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                    plugin.getLogger().log(java.util.logging.Level.SEVERE,
                            "[TF->MC] interrupted while waiting for entity scheduler nick=" + finalP.getName()
                                    + " uuid=" + uuid + " requestId=" + requestId,
                            interrupted);
                    writeJson(ex, 503, "{\"delivered\":false,\"online\":true,\"reason\":\"interrupted\"}");
                    return;
                }

                if (!delivered) {
                    writeJson(ex, 409, "{\"delivered\":false,\"online\":false,\"reason\":\"player_unavailable\"}");
                    return;
                }

                plugin.getLogger().info("[TF->MC] code delivery confirmed nick=" + finalP.getName()
                        + " uuid=" + uuid + " requestId=" + requestId + " ip=" + remoteIp);
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
