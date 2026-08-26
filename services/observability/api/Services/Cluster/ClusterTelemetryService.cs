using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskForge.Observability.Api.Services.Cluster;

public sealed class ClusterTelemetryService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ClusterTelemetryService> logger) : BackgroundService
{
    private sealed record AgentState(string NodeId, string Url, JsonObject? Payload, DateTimeOffset LastSuccessUtc, string? Error);

    private readonly ConcurrentDictionary<string, AgentState> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _seenEventIds = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _seenEventOrder = new();
    private readonly ConcurrentQueue<JsonObject> _controlEvents = new();
    private readonly ConcurrentDictionary<string, bool> _availability = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _everLive = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxSeenEventIds = 20_000;
    private const int MaxControlEvents = 500;
    private readonly HashSet<string> _seededNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _seedLock = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private string[] AgentUrls => (configuration["ClusterTelemetry:AgentUrls"] ?? string.Empty)
        .Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.TrimEnd('/'))
        .Where(x => Uri.TryCreate(x, UriKind.Absolute, out _))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private int PollSeconds => Math.Clamp(configuration.GetValue("ClusterTelemetry:PollSeconds", 5), 2, 60);

    private string LocalTelemetryPath => configuration["ClusterTelemetry:LocalTelemetryPath"] ?? string.Empty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cluster telemetry polling iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(PollSeconds), stoppingToken);
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        await PollLocalTelemetryAsync(ct);
        var urls = AgentUrls;
        if (urls.Length > 0) await Task.WhenAll(urls.Select(url => PollOneAsync(url, ct)));
        TrackAvailability();
        await FlushControlNotificationsAsync(ct);
    }

    private async Task PollLocalTelemetryAsync(CancellationToken ct)
    {
        var path = LocalTelemetryPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var payload = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("local telemetry is not an object");
            var nodeId = payload["node"]?["id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(nodeId)) return;
            _agents[nodeId] = new AgentState(nodeId, "local-file", payload, DateTimeOffset.UtcNow, null);
            SeedExpectedAgents(payload, nodeId);
            await ProcessEventsAsync(nodeId, payload, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to read local TaskForge node telemetry file.");
        }
    }


    private void SeedExpectedAgents(JsonObject localPayload, string localNodeId)
    {
        if (localPayload["topology"] is not JsonArray topology) return;
        foreach (var entry in topology.OfType<JsonObject>())
        {
            var nodeId = entry["id"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var url = entry["agent_url"]?.GetValue<string>()?.TrimEnd('/') ?? string.Empty;
            if (nodeId.Length == 0 || url.Length == 0 || string.Equals(nodeId, localNodeId, StringComparison.OrdinalIgnoreCase)) continue;
            _agents.TryAdd(nodeId, new AgentState(nodeId, url, null, DateTimeOffset.MinValue, "Node Agent ещё не ответил"));
        }
    }

    private async Task PollOneAsync(string baseUrl, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(4);
        try
        {
            using var response = await client.GetAsync(baseUrl + "/ha/telemetry", ct);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsStringAsync(ct);
            var payload = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("node agent returned a non-object payload");
            var nodeId = payload["node"]?["id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(nodeId)) throw new JsonException("node agent payload has no node.id");
            _agents.TryRemove("url:" + baseUrl, out _);
            _agents[nodeId] = new AgentState(nodeId, baseUrl, payload, DateTimeOffset.UtcNow, null);
            await ProcessEventsAsync(nodeId, payload, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RecordAgentFailure(baseUrl, "Node Agent timeout");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordAgentFailure(baseUrl, ex.Message);
        }
    }

    private void RecordAgentFailure(string baseUrl, string error)
    {
        var existing = _agents.FirstOrDefault(x => string.Equals(x.Value.Url, baseUrl, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(existing.Key))
            _agents[existing.Key] = existing.Value with { Error = error };
        else
            _agents["url:" + baseUrl] = new AgentState("url:" + baseUrl, baseUrl, null, DateTimeOffset.MinValue, error);
    }

    private void TrackAvailability()
    {
        var now = DateTimeOffset.UtcNow;
        var staleAfter = TimeSpan.FromSeconds(Math.Max(15, PollSeconds * 3));
        foreach (var state in _agents.Values)
        {
            if (state.NodeId.StartsWith("url:", StringComparison.OrdinalIgnoreCase)) continue;
            var live = state.Payload is not null && now - state.LastSuccessUtc <= staleAfter;
            if (live)
            {
                _everLive.TryAdd(state.NodeId, 0);
                if (_availability.TryGetValue(state.NodeId, out var previous) && !previous)
                    QueueControlEvent(state.NodeId, "cluster.node_recovered", "Нода снова доступна", $"Node Agent {state.NodeId} снова отвечает.", "success");
                _availability[state.NodeId] = true;
                continue;
            }

            // Never alert merely because observability-api has just started and a
            // configured peer has not answered yet. Alert only after that node
            // was observed healthy at least once in this process lifetime.
            if (!_everLive.ContainsKey(state.NodeId))
            {
                _availability.TryAdd(state.NodeId, false);
                continue;
            }
            if (_availability.TryGetValue(state.NodeId, out var wasLive) && wasLive)
                QueueControlEvent(state.NodeId, "cluster.node_down", "Нода недоступна", $"Node Agent {state.NodeId} перестал отвечать.", "error");
            _availability[state.NodeId] = false;
        }
    }

    private void QueueControlEvent(string nodeId, string kind, string title, string message, string severity)
    {
        var evt = new JsonObject
        {
            ["id"] = $"control-{kind}-{nodeId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["node"] = nodeId,
            ["kind"] = kind,
            ["severity"] = severity,
            ["title"] = title,
            ["message"] = message,
        };
        _controlEvents.Enqueue(evt);
        while (_controlEvents.Count > MaxControlEvents) _controlEvents.TryDequeue(out _);
    }

    private async Task FlushControlNotificationsAsync(CancellationToken ct)
    {
        foreach (var evt in _controlEvents)
        {
            var id = evt["id"]?.GetValue<string>() ?? string.Empty;
            if (id.Length == 0 || _seenEventIds.ContainsKey(id)) continue;
            var kind = evt["kind"]?.GetValue<string>() ?? string.Empty;
            var severity = evt["severity"]?.GetValue<string>() ?? "info";
            if (!ShouldNotify(kind, severity) || await NotifySupportBotAsync(evt, ct)) MarkEventSeen(id);
        }
    }

    private async Task ProcessEventsAsync(string nodeId, JsonObject payload, CancellationToken ct)
    {
        if (payload["events"] is not JsonArray events) return;
        bool seeded;
        lock (_seedLock)
        {
            seeded = _seededNodes.Contains(nodeId);
            if (!seeded) _seededNodes.Add(nodeId);
        }

        foreach (var node in events.OfType<JsonObject>())
        {
            var id = node["id"]?.GetValue<string>() ?? string.Empty;
            if (id.Length == 0 || _seenEventIds.ContainsKey(id)) continue;
            // After observability-api itself is replaced by Watchtower, allow only
            // very recent host-agent events through. This preserves the useful
            // "TaskForge updated" Telegram notification without replaying history.
            if (!seeded && !IsRecentEvent(node, TimeSpan.FromMinutes(3)))
            {
                MarkEventSeen(id);
                continue;
            }

            var kind = node["kind"]?.GetValue<string>() ?? string.Empty;
            var severity = node["severity"]?.GetValue<string>() ?? "info";
            if (!ShouldNotify(kind, severity))
            {
                MarkEventSeen(id);
                continue;
            }

            // Do not lose a notification just because support-bot is being replaced
            // by Watchtower at the same moment. Failed delivery is retried on the
            // next telemetry poll. support-bot itself de-duplicates by event id.
            if (await NotifySupportBotAsync(node, ct)) MarkEventSeen(id);
        }
    }


    private void MarkEventSeen(string id)
    {
        if (!_seenEventIds.TryAdd(id, 0)) return;
        _seenEventOrder.Enqueue(id);
        while (_seenEventIds.Count > MaxSeenEventIds && _seenEventOrder.TryDequeue(out var oldest))
            _seenEventIds.TryRemove(oldest, out _);
    }

    private static bool IsRecentEvent(JsonObject evt, TimeSpan window)
    {
        var raw = evt["at"]?.GetValue<string>();
        return DateTimeOffset.TryParse(raw, out var at) && DateTimeOffset.UtcNow - at <= window && at <= DateTimeOffset.UtcNow.AddSeconds(15);
    }

    private static bool ShouldNotify(string kind, string severity)
        => kind is "update.activated" or "update.watchtower_failed" or "update.watchtower_recovered"
           or "cluster.leader_changed" or "cluster.failback_requested" or "cluster.node_down" or "cluster.node_recovered"
           || kind.StartsWith("failover.", StringComparison.OrdinalIgnoreCase)
           || severity is "error";

    private async Task<bool> NotifySupportBotAsync(JsonObject evt, CancellationToken ct)
    {
        if (!configuration.GetValue("ClusterTelemetry:NotificationsEnabled", true)) return true;
        var baseUrl = (configuration["ClusterTelemetry:SupportBotUrl"] ?? string.Empty).TrimEnd('/');
        var key = configuration["ClusterTelemetry:InternalKey"] ?? string.Empty;
        if (baseUrl.Length == 0 || key.Length == 0) return true;
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/internal/cluster/events")
            {
                Content = JsonContent.Create(evt, options: _json)
            };
            req.Headers.TryAddWithoutValidation("X-Internal-Key", key);
            using var response = await client.SendAsync(req, ct);
            if (response.IsSuccessStatusCode) return true;
            logger.LogWarning("Support bot rejected cluster event with HTTP {Status}; it will be retried.", (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Support bot cluster-event delivery timed out; it will be retried.");
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to forward cluster event to support bot; it will be retried.");
            return false;
        }
    }

    public object BuildPublicSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var staleAfter = TimeSpan.FromSeconds(Math.Max(15, PollSeconds * 3));
        var states = _agents.Values.OrderBy(NodeSortKey, StringComparer.OrdinalIgnoreCase).ToList();
        var live = states.Where(x => x.Payload is not null && now - x.LastSuccessUtc <= staleAfter).ToList();
        var topologyMeta = ReadTopologyMeta(live.Select(x => x.Payload!));

        string? activeNode = live
            .Select(x => x.Payload!)
            .FirstOrDefault(x => x["ha"]?["traffic_ready"]?.GetValue<bool>() == true
                && IsPrimaryRole(x["ha"]?["role"]?.GetValue<string>()))?["node"]?["id"]?.GetValue<string>();
        activeNode ??= live.Select(x => x.Payload!).FirstOrDefault(x => IsPrimaryRole(x["ha"]?["role"]?.GetValue<string>()))?["node"]?["id"]?.GetValue<string>();

        var referenceImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reference = live.Select(x => x.Payload!).FirstOrDefault(x => string.Equals(x["node"]?["id"]?.GetValue<string>(), activeNode, StringComparison.OrdinalIgnoreCase))
            ?? live.Select(x => x.Payload!).FirstOrDefault(x => string.Equals(x["node"]?["app_profile"]?.GetValue<string>(), "full", StringComparison.OrdinalIgnoreCase));
        if (reference?["docker"]?["services"] is JsonArray referenceServices)
        {
            foreach (var svc in referenceServices.OfType<JsonObject>())
            {
                var name = svc["service"]?.GetValue<string>() ?? string.Empty;
                var fingerprint = svc["image_fingerprint"]?.GetValue<string>() ?? string.Empty;
                if (name.Length > 0 && fingerprint.Length > 0) referenceImages[name] = fingerprint;
            }
        }

        var sanitizedNodes = states.Select(state => BuildNode(state, now, staleAfter, activeNode, referenceImages, topologyMeta)).ToArray();
        var onlineCount = sanitizedNodes.Count(x => (bool)x["online"]!);
        var healthyCount = sanitizedNodes.Count(x => x.GetValueOrDefault("healthy") is true);
        var imageAssigned = 0;
        var imageSame = 0;
        var imageDifferent = 0;
        var imageMissing = 0;
        foreach (var node in sanitizedNodes)
        {
            if (node["services"] is not object[] services) continue;
            foreach (var obj in services.OfType<Dictionary<string, object?>>())
            {
                var status = obj.GetValueOrDefault("imageStatus")?.ToString();
                if (status == "not-assigned") continue;
                imageAssigned++;
                if (status == "same") imageSame++;
                else if (status == "different") imageDifferent++;
                else if (status == "missing") imageMissing++;
            }
        }

        var events = live.SelectMany(x => ReadEvents(x.Payload!))
            .Concat(ReadControlEvents())
            .OrderByDescending(x => x["at"]?.ToString())
            .Take(100)
            .ToArray();

        // A brief version skew while independent 5-minute updaters converge is
        // informational, not a cluster outage. Missing images / non-ready nodes
        // still make the cluster degraded because hot failover would be weaker.
        var imagesState = states.Count == 0 || onlineCount < states.Count || imageAssigned == 0
            ? "unknown"
            : imageDifferent == 0 && imageMissing == 0 ? "synchronized" : "attention";
        var overall = states.Count == 0 ? "unavailable" : healthyCount == states.Count && imageMissing == 0 ? "healthy" : "degraded";
        return new Dictionary<string, object?>
        {
            ["status"] = overall,
            ["generatedAt"] = now,
            ["summary"] = new
            {
                activeNode,
                nodesOnline = onlineCount,
                nodesHealthy = healthyCount,
                nodesTotal = states.Count,
                images = imagesState,
                imageSame,
                imageDifferent,
                imageMissing,
                quorum = live.Count(x => x.Payload?["node"]?["dcs_voter"]?.GetValue<bool>() == true),
                quorumTotal = topologyMeta.Values.Count(x => x["dcs_voter"]?.GetValue<bool>() == true),
                mode = live.Select(x => x.Payload?["node"]?["deployment_mode"]?.GetValue<string>()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "unknown",
            },
            ["nodes"] = sanitizedNodes,
            ["events"] = events,
        };
    }

    private static Dictionary<string, object?> BuildNode(AgentState state, DateTimeOffset now, TimeSpan staleAfter, string? activeNode, Dictionary<string, string> referenceImages, Dictionary<string, JsonObject> topologyMeta)
    {
        var p = state.Payload;
        var online = p is not null && now - state.LastSuccessUtc <= staleAfter;
        if (p is null)
        {
            topologyMeta.TryGetValue(state.NodeId, out var expected);
            return new Dictionary<string, object?>
            {
                ["id"] = state.NodeId,
                ["online"] = false,
                ["healthy"] = false,
                ["error"] = state.Error ?? "Нет свежей телеметрии",
                ["voter"] = expected?["dcs_voter"]?.GetValue<bool>() == true,
                ["canBePrimary"] = expected?["can_be_primary"]?.GetValue<bool>() == true,
                ["appProfile"] = expected?["app_profile"]?.GetValue<string>() ?? "unknown",
                ["role"] = "unreachable",
                ["appMode"] = "off",
                ["trafficReady"] = false,
                ["hotStartReady"] = false,
                ["isActive"] = false,
                ["services"] = Array.Empty<object>(),
                ["containers"] = Array.Empty<object>(),
            };
        }

        var id = p["node"]?["id"]?.GetValue<string>() ?? "?";
        var services = new List<Dictionary<string, object?>>();
        if (p["docker"]?["services"] is JsonArray arr)
        {
            foreach (var svc in arr.OfType<JsonObject>())
            {
                var name = svc["service"]?.GetValue<string>() ?? string.Empty;
                var assigned = svc["assigned"]?.GetValue<bool>() == true;
                var fingerprint = svc["image_fingerprint"]?.GetValue<string>() ?? string.Empty;
                string imageStatus;
                if (!assigned) imageStatus = "not-assigned";
                else if (fingerprint.Length == 0) imageStatus = "missing";
                else if (!referenceImages.TryGetValue(name, out var reference) || reference.Length == 0) imageStatus = "unknown";
                else imageStatus = string.Equals(reference, fingerprint, StringComparison.Ordinal) ? "same" : "different";

                services.Add(new Dictionary<string, object?>
                {
                    ["service"] = name,
                    ["assigned"] = assigned,
                    ["desiredState"] = svc["desired_state"]?.GetValue<string>(),
                    ["containerState"] = svc["container_state"]?.GetValue<string>(),
                    ["health"] = svc["health"]?.GetValue<string>(),
                    ["imageStatus"] = imageStatus,
                });
            }
        }

        var containers = new List<Dictionary<string, object?>>();
        if (p["docker"]?["containers"] is JsonArray carr)
        {
            foreach (var c in carr.OfType<JsonObject>())
            {
                containers.Add(new Dictionary<string, object?>
                {
                    ["service"] = c["service"]?.GetValue<string>(),
                    ["name"] = c["name"]?.GetValue<string>(),
                    ["state"] = c["state"]?.GetValue<string>(),
                    ["health"] = c["health"]?.GetValue<string>(),
                    ["restartCount"] = c["restart_count"]?.GetValue<int>() ?? 0,
                    ["startedAt"] = c["started_at"]?.GetValue<string>(),
                    ["cpu"] = c["cpu"]?.GetValue<string>(),
                    ["memory"] = c["memory"]?.GetValue<string>(),
                });
            }
        }

        var role = p["ha"]?["role"]?.GetValue<string>() ?? "unknown";
        var trafficReady = p["ha"]?["traffic_ready"]?.GetValue<bool>() == true;
        var hotStartReady = p["ha"]?["hot_start_ready"]?.GetValue<bool>() == true;
        var tlsReady = p["ha"]?["tls_ready"]?.GetValue<bool>() == true;
        var hotStartBlockers = p["ha"]?["hot_start_blockers"] is JsonArray blockers
            ? blockers.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
            : Array.Empty<string?>();
        var appProfile = p["node"]?["app_profile"]?.GetValue<string>() ?? "unknown";
        var isActive = string.Equals(id, activeNode, StringComparison.OrdinalIgnoreCase);
        var firewallActive = string.Equals(p["firewall"]?["status"]?.GetValue<string>(), "active", StringComparison.OrdinalIgnoreCase);
        var minioReady = p["minio"]?["ready"]?.GetValue<bool>() == true;
        var postgresHealthy = p["postgres"]?["healthy"]?.GetValue<bool>() == true;
        var roleReachable = !string.Equals(role, "unreachable", StringComparison.OrdinalIgnoreCase);
        var watchtowerRunning = p["update"]?["watchtower_running"]?.GetValue<bool>() == true;
        var updaterHasError = !string.IsNullOrWhiteSpace(p["update"]?["error"]?.GetValue<string>())
            || !string.IsNullOrWhiteSpace(p["update"]?["watchtower_error"]?.GetValue<string>());
        // A standby is only healthy for HA purposes when the application profile
        // assigned to it is actually hot-start ready. Otherwise the data node may
        // be alive, but failover capacity is degraded. A profile=none node is a
        // pure voter/data node and has no application readiness/updater requirement.
        var noAppProfile = string.Equals(appProfile, "none", StringComparison.OrdinalIgnoreCase);
        var applicationReady = isActive ? trafficReady : noAppProfile || hotStartReady;
        var updaterReady = noAppProfile || watchtowerRunning && !updaterHasError;
        var nodeHealthy = online && firewallActive && minioReady && postgresHealthy && roleReachable && applicationReady && updaterReady && (noAppProfile || tlsReady);

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["online"] = online,
            ["healthy"] = nodeHealthy,
            ["lastSeenAt"] = state.LastSuccessUtc,
            ["error"] = online ? null : state.Error ?? "Нет свежей телеметрии",
            ["priority"] = p["node"]?["priority"]?.GetValue<int>() ?? 0,
            ["preferred"] = p["node"]?["preferred"]?.GetValue<bool>() == true,
            ["voter"] = p["node"]?["dcs_voter"]?.GetValue<bool>() == true,
            ["canBePrimary"] = p["node"]?["can_be_primary"]?.GetValue<bool>() == true,
            ["appProfile"] = appProfile,
            ["deploymentMode"] = p["node"]?["deployment_mode"]?.GetValue<string>() ?? "unknown",
            ["bundleVersion"] = p["node"]?["bundle_version"]?.GetValue<string>() ?? string.Empty,
            ["bundleRevision"] = p["node"]?["bundle_revision"]?.GetValue<string>() ?? string.Empty,
            ["role"] = role,
            ["leader"] = p["ha"]?["leader"]?.GetValue<string>() ?? string.Empty,
            ["appMode"] = p["ha"]?["app_mode"]?.GetValue<string>() ?? "off",
            ["trafficReady"] = trafficReady,
            ["hotStartReady"] = hotStartReady,
            ["hotStartBlockers"] = hotStartBlockers,
            ["tlsReady"] = tlsReady,
            ["isActive"] = isActive,
            ["host"] = SanitizeNode(p["host"]),
            ["firewall"] = SanitizeNode(p["firewall"]),
            ["wireguard"] = SanitizeNode(p["wireguard"]),
            ["postgres"] = SanitizeNode(p["postgres"]),
            ["minio"] = SanitizeNode(p["minio"]),
            ["update"] = BuildPublicUpdate(p["update"] as JsonObject),
            ["docker"] = new
            {
                containerCount = p["docker"]?["container_count"]?.GetValue<int>() ?? 0,
                assignedAppCount = p["docker"]?["assigned_app_count"]?.GetValue<int>() ?? 0,
                preparedAppCount = p["docker"]?["prepared_app_count"]?.GetValue<int>() ?? 0,
                imagesReady = p["docker"]?["images_ready"]?.GetValue<int>() ?? 0,
                imagesMissing = p["docker"]?["images_missing"]?.GetValue<int>() ?? 0,
            },
            ["services"] = services.ToArray(),
            ["containers"] = containers.ToArray(),
        };
    }

    private static Dictionary<string, JsonObject> ReadTopologyMeta(IEnumerable<JsonObject> payloads)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in payloads)
        {
            if (payload["topology"] is not JsonArray topology) continue;
            foreach (var entry in topology.OfType<JsonObject>())
            {
                var id = entry["id"]?.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(id)) result[id] = entry;
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    private static object? SanitizeNode(JsonNode? node)
        => node is null ? null : JsonSerializer.Deserialize<object>(node.ToJsonString());

    private static object? BuildPublicUpdate(JsonObject? update)
    {
        if (update is null) return null;
        var changed = update["changed_services"] is JsonArray services
            ? services.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
            : Array.Empty<string?>();
        var hasError = !string.IsNullOrWhiteSpace(update["error"]?.GetValue<string>())
            || !string.IsNullOrWhiteSpace(update["watchtower_error"]?.GetValue<string>());
        return new
        {
            mode = update["mode"]?.GetValue<string>() ?? "unknown",
            watchtowerRunning = update["watchtower_running"]?.GetValue<bool>() == true,
            status = update["status"]?.GetValue<string>() ?? "unknown",
            startedAt = update["started_at"]?.GetValue<string>(),
            completedAt = update["completed_at"]?.GetValue<string>(),
            changedServices = changed,
            pollIntervalSeconds = update["poll_interval_seconds"]?.GetValue<int>() ?? 0,
            hasError,
        };
    }

    private static IEnumerable<Dictionary<string, object?>> ReadEvents(JsonObject p)
    {
        if (p["events"] is not JsonArray events) yield break;
        foreach (var e in events.OfType<JsonObject>()) yield return BuildPublicEvent(e);
    }

    private IEnumerable<Dictionary<string, object?>> ReadControlEvents()
    {
        foreach (var e in _controlEvents) yield return BuildPublicEvent(e);
    }

    private static Dictionary<string, object?> BuildPublicEvent(JsonObject e)
        => new()
        {
            ["id"] = e["id"]?.GetValue<string>(),
            ["at"] = e["at"]?.GetValue<string>(),
            ["node"] = e["node"]?.GetValue<string>(),
            ["kind"] = e["kind"]?.GetValue<string>(),
            ["severity"] = e["severity"]?.GetValue<string>(),
            ["title"] = e["title"]?.GetValue<string>(),
            ["message"] = e["message"]?.GetValue<string>(),
        };

    private static bool IsPrimaryRole(string? role)
        => role is not null && (role.Equals("primary", StringComparison.OrdinalIgnoreCase)
            || role.Equals("leader", StringComparison.OrdinalIgnoreCase)
            || role.Equals("master", StringComparison.OrdinalIgnoreCase));

    private static string NodeSortKey(AgentState state)
        => state.Payload?["node"]?["id"]?.GetValue<string>() ?? state.NodeId;
}
