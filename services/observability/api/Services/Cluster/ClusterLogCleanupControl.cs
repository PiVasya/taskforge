using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskForge.Observability.Api.Services.Cluster;

public sealed record ClusterLogCleanupRequest(IReadOnlyCollection<string>? Nodes);

public sealed record ClusterLogCleanupNodeResult(
    string Node,
    bool Success,
    long ReclaimedBytes,
    long FilesystemFreeDeltaBytes,
    int ContainersCleared,
    int ContainersSkipped,
    int HostLogFilesCleared,
    bool JournalOk,
    string? Message = null,
    string? Code = null);

public sealed record ClusterLogCleanupBatchResult(
    IReadOnlyList<ClusterLogCleanupNodeResult> Nodes,
    long ReclaimedBytes,
    DateTimeOffset FinishedAt);

public sealed class ClusterLogCleanupException : Exception
{
    public ClusterLogCleanupException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}

public sealed partial class ClusterTelemetryService
{
    private const string LogCleanupTokenPurpose = "taskforge-cluster-agent-maintenance-v1";
    private const int LogCleanupMinAgentRevision = 73;
    private sealed record LogCleanupTarget(string NodeId, string BaseUrl, string Token, string? UnixSocketPath);

    public async Task<ClusterLogCleanupBatchResult> CleanupLogsAsync(ClusterLogCleanupRequest request, CancellationToken ct)
    {
        var requestedNodes = (request.Nodes ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        if (requestedNodes.Length == 0)
            throw new ClusterLogCleanupException(StatusCodes.Status400BadRequest, "LOG_CLEANUP_NODE_REQUIRED", "Выберите хотя бы одну ноду для очистки логов.");

        var token = LogCleanupControlToken();
        var known = _agents.Values
            .Where(x => !x.NodeId.StartsWith("url:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Url == "local-file" ? 0 : 1).First(), StringComparer.OrdinalIgnoreCase);

        var tasks = requestedNodes.Select(async nodeId =>
        {
            if (!known.TryGetValue(nodeId, out var state))
                return FailedLogCleanup(nodeId, "LOG_CLEANUP_NODE_UNKNOWN", "Нода отсутствует в текущей топологии.");

            var revision = AgentRevision(state);
            if (revision is null || revision < LogCleanupMinAgentRevision)
                return FailedLogCleanup(nodeId, "LOG_CLEANUP_AGENT_TOO_OLD", $"Node Agent r{revision?.ToString() ?? "?"} не поддерживает очистку логов. Требуется r{LogCleanupMinAgentRevision}+.");

            if (state.LastLiveSuccessUtc == DateTimeOffset.MinValue || DateTimeOffset.UtcNow - state.LastLiveSuccessUtc > TimeSpan.FromSeconds(DownAfterSeconds))
                return FailedLogCleanup(nodeId, "LOG_CLEANUP_NODE_OFFLINE", "Node Agent ноды сейчас недоступен.");

            LogCleanupTarget target;
            try
            {
                target = ResolveLogCleanupTarget(state, known.Values, token);
            }
            catch (ClusterLogCleanupException ex)
            {
                return FailedLogCleanup(nodeId, ex.Code, ex.Message);
            }

            try
            {
                using var client = CreateLogCleanupClient(target);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                using var message = new HttpRequestMessage(HttpMethod.Post, target.BaseUrl + "/ha/logs/cleanup")
                {
                    Content = JsonContent.Create(new { })
                };
                message.Headers.TryAddWithoutValidation("X-TaskForge-Cluster-Token", target.Token);
                using var response = await client.SendAsync(message, timeout.Token);
                var raw = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var payload = TryParseJson(raw);
                    var detail = ReadString(payload, "message") ?? ReadString(payload, "error") ?? $"Node Agent вернул HTTP {(int)response.StatusCode}.";
                    var code = ReadString(payload, "code") ?? "LOG_CLEANUP_AGENT_REJECTED";
                    ClusterLogger.LogWarning("Log cleanup rejected by Node Agent {Node}: HTTP {Status} code={Code} message={Message}", nodeId, (int)response.StatusCode, code, detail);
                    return FailedLogCleanup(nodeId, code, detail);
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var docker = ObjectProperty(root, "docker");
                var hostLogs = ObjectProperty(root, "host_logs");
                var journal = ObjectProperty(root, "journal");
                var reclaimed = Int64Property(root, "reclaimed_bytes");
                var freeDelta = Int64Property(root, "filesystem_free_delta_bytes");
                var containersCleared = Int32Property(docker, "containers_cleared");
                var containersSkipped = Int32Property(docker, "containers_skipped");
                var hostFiles = Int32Property(hostLogs, "files_cleared");
                var journalOk = BoolProperty(journal, "ok");
                var transport = target.UnixSocketPath is null ? "wireguard-http" : "local-unix";
                Console.WriteLine($"[TFLOG API CLEANUP] node={nodeId} transport={transport} reclaimed={reclaimed} freeDelta={freeDelta} utc={DateTimeOffset.UtcNow:O}");
                return new ClusterLogCleanupNodeResult(nodeId, true, reclaimed, freeDelta, containersCleared, containersSkipped, hostFiles, journalOk);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return FailedLogCleanup(nodeId, "LOG_CLEANUP_AGENT_TIMEOUT", "Node Agent не завершил очистку логов вовремя.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ClusterLogger.LogWarning(ex, "Unable to clean logs on node {Node}", nodeId);
                return FailedLogCleanup(nodeId, "LOG_CLEANUP_AGENT_UNAVAILABLE", "Не удалось выполнить очистку логов через Node Agent.");
            }
        });

        var results = await Task.WhenAll(tasks);
        return new ClusterLogCleanupBatchResult(results, results.Where(x => x.Success).Sum(x => x.ReclaimedBytes), DateTimeOffset.UtcNow);
    }

    private static ClusterLogCleanupNodeResult FailedLogCleanup(string node, string code, string message)
        => new(node, false, 0, 0, 0, 0, 0, false, message, code);

    private LogCleanupTarget ResolveLogCleanupTarget(AgentState state, IEnumerable<AgentState> states, string token)
    {
        if (!string.IsNullOrWhiteSpace(_localNodeId) && string.Equals(state.NodeId, _localNodeId, StringComparison.OrdinalIgnoreCase))
        {
            var socketPath = LocalAgentSocketPath.Trim();
            if (socketPath.Length == 0 || !Path.IsPathRooted(socketPath))
                throw new ClusterLogCleanupException(StatusCodes.Status503ServiceUnavailable, "LOG_CLEANUP_LOCAL_SOCKET_INVALID", "Локальный Unix socket Node Agent не настроен.");
            return new LogCleanupTarget(state.NodeId, "http://localhost", token, socketPath);
        }

        var url = ResolveAgentUrl(state, states);
        if (string.IsNullOrWhiteSpace(url))
            throw new ClusterLogCleanupException(StatusCodes.Status409Conflict, "LOG_CLEANUP_AGENT_URL_MISSING", "Не удалось определить адрес Node Agent.");
        return new LogCleanupTarget(state.NodeId, url.TrimEnd('/'), token, null);
    }

    private HttpClient CreateLogCleanupClient(LogCleanupTarget target)
    {
        if (target.UnixSocketPath is null)
            return ClusterHttpClientFactory.CreateClient();

        var endpoint = new UnixDomainSocketEndPoint(target.UnixSocketPath);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private string LogCleanupControlToken()
    {
        var key = ClusterConfiguration["ClusterTelemetry:InternalKey"]
            ?? ClusterConfiguration["InternalApi:Key"]
            ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY")
            ?? string.Empty;
        if (key.Length < 40)
            throw new ClusterLogCleanupException(StatusCodes.Status503ServiceUnavailable, "LOG_CLEANUP_AUTH_NOT_CONFIGURED", "В observability-api не настроен внутренний ключ управления Node Agent.");
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(LogCleanupTokenPurpose));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static JsonElement ObjectProperty(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static long Int64Property(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return Math.Max(0, number);
        return long.TryParse(value.ToString(), out var parsed) ? Math.Max(0, parsed) : 0;
    }

    private static int Int32Property(JsonElement parent, string name)
        => (int)Math.Min(int.MaxValue, Int64Property(parent, name));

    private static bool BoolProperty(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
