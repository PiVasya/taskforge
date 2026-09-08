using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskForge.Observability.Api.Services.Cluster;

public sealed record ClusterPrimarySwitchOutcome(
    bool Accepted,
    bool Changed,
    string From,
    string Target,
    long LagBytes,
    long LagLimitBytes,
    bool EdgeRequired,
    string RequestId,
    string Message);

public sealed class ClusterPrimarySwitchException : Exception
{
    public ClusterPrimarySwitchException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}

public sealed partial class ClusterTelemetryService
{
    private readonly SemaphoreSlim _primarySwitchGate = new(1, 1);
    private const long DefaultMaximumSwitchoverLagBytes = 16_777_216;

    public async Task<ClusterPrimarySwitchOutcome> RequestPrimarySwitchAsync(string? requestedTarget, CancellationToken ct)
    {
        var target = (requestedTarget ?? string.Empty).Trim();
        if (target.Length is < 1 or > 32 || target.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ClusterPrimarySwitchException(StatusCodes.Status400BadRequest, "INVALID_PRIMARY_TARGET", "Некорректный идентификатор целевой ноды.");

        if (!await _primarySwitchGate.WaitAsync(0, ct))
            throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "PRIMARY_SWITCH_IN_PROGRESS", "Другой запрос смены Primary уже выполняется.");

        try
        {
            var now = DateTimeOffset.UtcNow;
            if (PrimaryTransitionActive(now))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "PRIMARY_TRANSITION_IN_PROGRESS", "Предыдущее переключение Primary ещё находится в переходном окне. Дождитесь полной готовности и обновите состояние.");

            var downAfter = TimeSpan.FromSeconds(DownAfterSeconds);
            var telemetryStaleAfter = TimeSpan.FromSeconds(Math.Max(90, TelemetryPollSeconds * 3));
            var states = _agents.Values
                .Where(x => x.Payload is not null)
                .OrderBy(NodeSortKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool Fresh(AgentState state) =>
                state.LastLiveSuccessUtc != DateTimeOffset.MinValue && now - state.LastLiveSuccessUtc <= downAfter
                && state.LastTelemetrySuccessUtc != DateTimeOffset.MinValue && now - state.LastTelemetrySuccessUtc <= telemetryStaleAfter;

            var live = states.Where(Fresh).ToArray();
            var leaders = live
                .Where(x => IsPrimaryRole(ClusterTelemetryNormalizer.Text(x.Payload?["ha"]?["role"])))
                .Select(x => x.NodeId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (leaders.Length != 1)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "PRIMARY_NOT_UNIQUE", $"Нельзя переключать Primary: подтверждено основных нод — {leaders.Length}.");

            var leader = leaders[0];
            var leaderState = live.First(x => string.Equals(x.NodeId, leader, StringComparison.OrdinalIgnoreCase));
            var localState = states.FirstOrDefault(x => string.Equals(x.Url, "local-file", StringComparison.OrdinalIgnoreCase));
            if (localState is null || !Fresh(localState) || !string.Equals(localState.NodeId, leader, StringComparison.OrdinalIgnoreCase))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "CONTROL_NOT_ON_PRIMARY", "Управляющий API сейчас не подтверждён на текущей Primary. Дождитесь завершения маршрутизации и обновите страницу.");

            var leaderHa = leaderState.Payload?["ha"] as JsonObject;
            if (leaderHa?["reconciled"]?.GetValue<bool>() != true || leaderHa?["traffic_ready"]?.GetValue<bool>() != true)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "PRIMARY_NOT_READY", $"Сервер {leader} ещё не находится в полностью готовом состоянии.");
            if (leaderHa?["control_plane_fenced"]?.GetValue<bool>() == true)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "PRIMARY_FENCED", $"Сервер {leader} временно fenced; переключение запрещено.");

            var targetState = states.FirstOrDefault(x => string.Equals(x.NodeId, target, StringComparison.OrdinalIgnoreCase));
            if (targetState is null)
                throw new ClusterPrimarySwitchException(StatusCodes.Status400BadRequest, "UNKNOWN_PRIMARY_TARGET", $"Нода {target} отсутствует в телеметрии кластера.");
            target = targetState.NodeId;

            if (string.Equals(target, leader, StringComparison.OrdinalIgnoreCase))
                return new ClusterPrimarySwitchOutcome(true, false, leader, target, 0, MaximumLagBytes(), EdgeRequired(live), "already-primary", $"Сервер {target} уже является Primary.");

            if (!Fresh(targetState))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_NOT_FRESH", $"Нода {target} не имеет свежей телеметрии и не может быть выбрана Primary.");

            var targetPayload = targetState.Payload!;
            var targetNode = targetPayload["node"] as JsonObject;
            var targetHa = targetPayload["ha"] as JsonObject;
            var targetPostgres = targetPayload["postgres"] as JsonObject;
            if (targetNode?["can_be_primary"]?.GetValue<bool>() != true)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_CANNOT_BE_PRIMARY", $"Нода {target} настроена с can_be_primary=false.");
            if (targetHa?["reconciled"]?.GetValue<bool>() != true || targetHa?["control_plane_fenced"]?.GetValue<bool>() == true)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_NOT_RECONCILED", $"Node Agent {target} не reconciled или fenced.");

            var targetRole = ClusterTelemetryNormalizer.Text(targetHa?["role"])?.ToLowerInvariant() ?? string.Empty;
            if (targetRole is not ("standby" or "replica" or "slave"))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_NOT_STANDBY", $"Нода {target} сейчас имеет роль {targetRole}, а не standby/replica.");
            if (!string.Equals(ClusterTelemetryNormalizer.Text(targetHa?["leader"]), leader, StringComparison.OrdinalIgnoreCase))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_LEADER_MISMATCH", $"Нода {target} не подтверждает текущую Primary {leader}.");
            if (targetHa?["hot_start_ready"]?.GetValue<bool>() != true)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_NOT_HOT_READY", $"Нода {target} не готова к горячему запуску.");
            if (targetPostgres?["healthy"]?.GetValue<bool>() != true)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_POSTGRES_NOT_READY", $"PostgreSQL на ноде {target} не готов к переключению.");

            var sourceRevision = ClusterTelemetryNormalizer.Text(leaderState.Payload?["node"]?["bundle_revision"]) ?? string.Empty;
            var targetRevision = ClusterTelemetryNormalizer.Text(targetNode?["bundle_revision"]) ?? string.Empty;
            if (sourceRevision.Length == 0 || targetRevision.Length == 0 || !string.Equals(sourceRevision, targetRevision, StringComparison.Ordinal))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "AGENT_REVISION_MISMATCH", $"Версии Node Agent не совпадают: {leader}=r{sourceRevision}, {target}=r{targetRevision}.");
            if (!int.TryParse(targetRevision, out var numericRevision) || numericRevision < 57)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "AGENT_TOO_OLD", $"Для управления через сайт нужен Node Agent r57 или новее; на {target} установлена r{targetRevision}.");

            var edgeRequired = EdgeRequired(live);
            if (edgeRequired && targetPayload["edge"]?["configured"]?.GetValue<bool>() != true)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_EDGE_NOT_CONFIGURED", $"Cloudflare failover настроен в кластере, но на ноде {target} нет edge-конфигурации.");

            // This endpoint is deliberately allowed only on the node that is
            // currently confirmed as Primary (CONTROL_NOT_ON_PRIMARY above).
            // Therefore the leader Patroni is the local Compose `postgres` service.
            // Calling the host's own WireGuard-published port from a Docker bridge
            // can hit self-hairpin/NAT filtering and time out even though the same
            // endpoint works from the host. Use the Docker-local route instead.
            var (leaderHost, leaderPort) = LocalPatroniEndpoint();
            ClusterLogger.LogInformation(
                "Admin primary switchover preflight uses local Patroni endpoint {Host}:{Port} for leader {Leader}.",
                leaderHost, leaderPort, leader);
            var cluster = await ReadPatroniClusterAsync(leaderHost, leaderPort, ct);
            var patroniLeaders = (cluster["members"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(x => IsPrimaryRole(ClusterTelemetryNormalizer.Text(x["role"])))
                .Select(x => ClusterTelemetryNormalizer.Text(x["name"]))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (patroniLeaders.Length != 1 || !string.Equals(patroniLeaders[0], leader, StringComparison.OrdinalIgnoreCase))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "PATRONI_LEADER_MISMATCH", "Patroni не подтверждает единственную Primary, совпадающую с телеметрией.");

            var member = (cluster["members"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(x => string.Equals(ClusterTelemetryNormalizer.Text(x["name"]), target, StringComparison.OrdinalIgnoreCase));
            if (member is null)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_NOT_IN_PATRONI", $"Нода {target} отсутствует в Patroni /cluster.");

            var memberRole = (ClusterTelemetryNormalizer.Text(member["role"]) ?? string.Empty).ToLowerInvariant();
            var memberState = (ClusterTelemetryNormalizer.Text(member["state"]) ?? string.Empty).ToLowerInvariant();
            if (memberRole is not ("replica" or "standby" or "slave") || memberState is not ("running" or "streaming"))
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_REPLICA_NOT_HEALTHY", $"Patroni: {target} не является здоровой репликой (role={memberRole}, state={memberState}).");

            var lagValue = ClusterTelemetryNormalizer.Number(member["lag"]);
            if (!lagValue.HasValue)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_LAG_UNKNOWN", $"Patroni не сообщил отставание реплики {target}; безопасное переключение отменено.");
            var lag = checked((long)lagValue.Value);
            var lagLimit = MaximumLagBytes();
            if (lag < 0 || lag > lagLimit)
                throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "TARGET_LAG_TOO_HIGH", $"Отставание реплики {target}: {lag} байт, допустимо не более {lagLimit} байт.");

            var internalKey = ClusterConfiguration["ClusterTelemetry:InternalKey"]
                ?? ClusterConfiguration["InternalApi:Key"]
                ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY")
                ?? string.Empty;
            if (internalKey.Length < 40)
                throw new ClusterPrimarySwitchException(StatusCodes.Status503ServiceUnavailable, "PATRONI_AUTH_NOT_CONFIGURED", "В observability-api отсутствует корректный внутренний ключ для безопасного Patroni switchover.");

            var requestId = $"primary-switch-{leader}-{target}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            await PostPatroniSwitchoverAsync(leaderHost, leaderPort, leader, target, internalKey, ct);
            BeginPrimaryTransition(leader, target);

            var notification = new JsonObject
            {
                ["id"] = requestId,
                ["at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["node"] = target,
                ["kind"] = "cluster.primary_switch_requested",
                ["severity"] = "info",
                ["title"] = "Запрошено переключение Primary",
                ["message"] = $"{leader} → {target}"
            };
            _controlEvents.Enqueue((JsonObject)notification.DeepClone());
            while (_controlEvents.Count > MaxControlEvents) _controlEvents.TryDequeue(out _);
            // Return to the browser immediately after Patroni accepts. Waiting on
            // Telegram here is unsafe: the old Primary can stop this very
            // observability-api container while the HTTP response is in flight.
            // The background relay may still deliver this request event, while
            // the new Primary will reliably emit leader_changed + edge.public_ready.

            ClusterLogger.LogWarning("Admin primary switchover accepted: {From} -> {Target}, lag={Lag} bytes, edgeRequired={EdgeRequired}, request={RequestId}", leader, target, lag, edgeRequired, requestId);
            return new ClusterPrimarySwitchOutcome(true, true, leader, target, lag, lagLimit, edgeRequired, requestId,
                $"Patroni принял переключение {leader} → {target}. Страница будет ждать готовность приложений, Cloudflare и HTTPS.");
        }
        finally
        {
            _primarySwitchGate.Release();
        }
    }

    private long MaximumLagBytes()
        => Math.Clamp(ClusterConfiguration.GetValue<long?>("ClusterControl:MaximumSwitchoverLagBytes") ?? DefaultMaximumSwitchoverLagBytes, 0, 1L << 30);

    private static bool EdgeRequired(IEnumerable<AgentState> states)
        => states.Any(x => x.Payload?["edge"]?["configured"]?.GetValue<bool>() == true);

    private (string Host, int Port) LocalPatroniEndpoint()
    {
        var host = (ClusterConfiguration["ClusterControl:LocalPatroniHost"] ?? "postgres").Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "postgres";
        var port = ClusterConfiguration.GetValue<int?>("ClusterControl:LocalPatroniPort") ?? 8008;
        if (port is < 1 or > 65535)
            throw new ClusterPrimarySwitchException(
                StatusCodes.Status500InternalServerError,
                "PATRONI_LOCAL_ENDPOINT_INVALID",
                "Некорректно настроен локальный адрес Patroni для управляющего API.");
        return (host, port);
    }

    private static (string Host, int Port) PatroniEndpoint(AgentState state, IEnumerable<AgentState> allStates)
    {
        var host = ClusterTelemetryNormalizer.Text(state.Payload?["node"]?["wireguard_ip"]);
        int? port = null;
        foreach (var source in allStates)
        {
            var topology = source.Payload?["topology"] as JsonArray;
            var meta = topology?.OfType<JsonObject>().FirstOrDefault(x => string.Equals(ClusterTelemetryNormalizer.Text(x["id"]), state.NodeId, StringComparison.OrdinalIgnoreCase));
            host ??= ClusterTelemetryNormalizer.Text(meta?["wireguard_ip"]);
            var portValue = ClusterTelemetryNormalizer.Number(meta?["patroni_rest_port"]);
            if (!port.HasValue && portValue is >= 1 and <= 65535) port = (int)portValue.Value;
            if (!string.IsNullOrWhiteSpace(host) && port.HasValue) break;
        }
        if (string.IsNullOrWhiteSpace(host))
            throw new ClusterPrimarySwitchException(StatusCodes.Status409Conflict, "PATRONI_ADDRESS_MISSING", $"Не удалось определить WireGuard-адрес Patroni для {state.NodeId}.");
        return (host, port.GetValueOrDefault(8008));
    }

    private async Task<JsonObject> ReadPatroniClusterAsync(string host, int port, CancellationToken ct)
    {
        var client = ClusterHttpClientFactory.CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var response = await client.GetAsync(BuildPatroniUri(host, port, "/cluster"), timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new ClusterPrimarySwitchException(StatusCodes.Status502BadGateway, "PATRONI_CLUSTER_UNAVAILABLE", $"Patroni /cluster вернул HTTP {(int)response.StatusCode}.");
            var raw = await response.Content.ReadAsStringAsync(timeout.Token);
            return JsonNode.Parse(raw) as JsonObject
                ?? throw new ClusterPrimarySwitchException(StatusCodes.Status502BadGateway, "PATRONI_CLUSTER_INVALID", "Patroni /cluster вернул некорректный ответ.");
        }
        catch (ClusterPrimarySwitchException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClusterPrimarySwitchException(StatusCodes.Status504GatewayTimeout, "PATRONI_CLUSTER_TIMEOUT", "Patroni /cluster не ответил вовремя.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ClusterLogger.LogWarning(ex, "Unable to read Patroni cluster before admin switchover.");
            throw new ClusterPrimarySwitchException(StatusCodes.Status502BadGateway, "PATRONI_CLUSTER_UNAVAILABLE", "Не удалось прочитать состояние Patroni перед переключением.");
        }
    }

    private async Task PostPatroniSwitchoverAsync(string host, int port, string leader, string target, string internalKey, CancellationToken ct)
    {
        var digest = HMACSHA512.HashData(Encoding.UTF8.GetBytes(internalKey), Encoding.UTF8.GetBytes("taskforge-patroni-rest-v2"));
        var password = Convert.ToHexString(digest).ToLowerInvariant()[..64];
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"taskforge_patroni:{password}"));
        var client = ClusterHttpClientFactory.CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildPatroniUri(host, port, "/switchover"))
        {
            Content = JsonContent.Create(new { leader, candidate = target })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if ((int)response.StatusCode is 200 or 202) return;
            var detail = (await response.Content.ReadAsStringAsync(timeout.Token)).Trim();
            if (detail.Length > 400) detail = detail[..400];
            ClusterLogger.LogWarning("Patroni rejected admin switchover {Leader}->{Target}: HTTP {Status} {Detail}", leader, target, (int)response.StatusCode, detail);
            throw new ClusterPrimarySwitchException(StatusCodes.Status502BadGateway, "PATRONI_SWITCH_REJECTED", $"Patroni отклонил переключение {leader} → {target} (HTTP {(int)response.StatusCode}).");
        }
        catch (ClusterPrimarySwitchException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClusterPrimarySwitchException(StatusCodes.Status504GatewayTimeout, "PATRONI_SWITCH_TIMEOUT", "Patroni не подтвердил запрос переключения вовремя. Не повторяйте запрос вслепую — сначала обновите состояние кластера.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ClusterLogger.LogWarning(ex, "Patroni switchover request failed for {Leader}->{Target}", leader, target);
            throw new ClusterPrimarySwitchException(StatusCodes.Status502BadGateway, "PATRONI_SWITCH_FAILED", "Не удалось отправить запрос переключения Patroni. Обновите состояние перед повторной попыткой.");
        }
    }

    private static Uri BuildPatroniUri(string host, int port, string path)
    {
        var builder = new UriBuilder("http", host, port, path);
        return builder.Uri;
    }
}
