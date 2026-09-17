using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskForge.Observability.Api.Services.Cluster;

public sealed record ClusterDiagnosticsRequest(
    IReadOnlyCollection<string>? Nodes,
    string? Mode,
    string? Since,
    int? MaxLogMb);

public sealed record ClusterDiagnosticsJobResult(
    string Node,
    bool Accepted,
    string? JobId,
    string Status,
    string? Message,
    string? Code = null);

public sealed record ClusterDiagnosticsBatchResult(
    IReadOnlyList<ClusterDiagnosticsJobResult> Jobs,
    string Mode,
    string Since,
    int MaxLogMb);

public sealed record ClusterDiagnosticsArchiveListResult(
    IReadOnlyList<ClusterDiagnosticsStoredArchive> Archives,
    int RetentionDays,
    DateTimeOffset GeneratedAt);

public sealed class ClusterDiagnosticsException : Exception
{
    public ClusterDiagnosticsException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}

public sealed partial class ClusterTelemetryService
{
    private const string DiagnosticsTokenPurpose = "taskforge-cluster-agent-diagnostics-v1";
    private const int DiagnosticsMinAgentRevision = 65;
    private static readonly JsonSerializerOptions DiagnosticsJson = new(JsonSerializerDefaults.Web);
    private sealed record DiagnosticsTarget(string NodeId, string BaseUrl, string Token, string? UnixSocketPath);
    private sealed record TrackedDiagnosticsArchiveJob(string Node, string JobId, DateTimeOffset AcceptedAt, DateTimeOffset NextAttemptAt);
    private readonly ConcurrentDictionary<string, TrackedDiagnosticsArchiveJob> _diagnosticsArchiveJobs = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastDiagnosticsArchiveCleanupUtc = DateTimeOffset.MinValue;

    private string LocalAgentSocketPath => ClusterConfiguration["ClusterTelemetry:LocalAgentSocketPath"]
        ?? "/run/taskforge-cluster-host/node-agent.sock";

    public async Task<ClusterDiagnosticsBatchResult> StartDiagnosticsAsync(ClusterDiagnosticsRequest request, CancellationToken ct)
    {
        var mode = NormalizeDiagnosticsMode(request.Mode);
        var since = NormalizeDiagnosticsSince(request.Since);
        var maxLogMb = Math.Clamp(request.MaxLogMb ?? 32, 1, 512);
        var requestedNodes = (request.Nodes ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        if (requestedNodes.Length == 0)
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_NODE_REQUIRED", "Выберите хотя бы одну ноду для сбора логов.");

        var token = DiagnosticsControlToken();
        var known = _agents.Values
            .Where(x => !x.NodeId.StartsWith("url:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Url == "local-file" ? 0 : 1).First(), StringComparer.OrdinalIgnoreCase);

        var tasks = requestedNodes.Select(async nodeId =>
        {
            if (!known.TryGetValue(nodeId, out var state))
                return new ClusterDiagnosticsJobResult(nodeId, false, null, "failed", "Нода отсутствует в текущей топологии.", "DIAGNOSTICS_NODE_UNKNOWN");

            var revision = AgentRevision(state);
            if (revision is null || revision < DiagnosticsMinAgentRevision)
                return new ClusterDiagnosticsJobResult(nodeId, false, null, "failed", $"Node Agent r{revision?.ToString() ?? "?"} не поддерживает сбор логов. Требуется r{DiagnosticsMinAgentRevision}+.", "DIAGNOSTICS_AGENT_TOO_OLD");

            DiagnosticsTarget target;
            try
            {
                target = ResolveDiagnosticsTarget(state, known.Values, token);
            }
            catch (ClusterDiagnosticsException ex)
            {
                return new ClusterDiagnosticsJobResult(nodeId, false, null, "failed", ex.Message, ex.Code);
            }

            try
            {
                using var client = CreateDiagnosticsClient(target);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var message = new HttpRequestMessage(HttpMethod.Post, target.BaseUrl + "/ha/diagnostics")
                {
                    Content = JsonContent.Create(new { mode, since, max_log_mb = maxLogMb })
                };
                message.Headers.TryAddWithoutValidation("X-TaskForge-Cluster-Token", target.Token);
                using var response = await client.SendAsync(message, timeout.Token);
                var raw = await response.Content.ReadAsStringAsync(timeout.Token);
                var payload = TryParseJson(raw);
                var jobId = ReadString(payload, "job_id");
                var status = ReadString(payload, "status") ?? (response.IsSuccessStatusCode ? "queued" : "failed");
                var detail = ReadString(payload, "message") ?? ReadString(payload, "error");
                var code = ReadString(payload, "code");
                if (!response.IsSuccessStatusCode)
                {
                    ClusterLogger.LogWarning("Diagnostics start rejected by Node Agent {Node}: HTTP {Status} code={Code} message={Message}", nodeId, (int)response.StatusCode, code, detail);
                    return new ClusterDiagnosticsJobResult(nodeId, false, jobId, status, detail ?? $"Node Agent вернул HTTP {(int)response.StatusCode}.", code ?? "DIAGNOSTICS_AGENT_REJECTED");
                }

                var transport = target.UnixSocketPath is null ? "wireguard-http" : "local-unix";
                Console.WriteLine($"[TFDIAG API START] node={nodeId} job={jobId ?? "-"} transport={transport} mode={mode} since={since} maxLogMb={maxLogMb} utc={DateTimeOffset.UtcNow:O}");
                return new ClusterDiagnosticsJobResult(nodeId, true, jobId, status, detail, code);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ClusterDiagnosticsJobResult(nodeId, false, null, "failed", "Node Agent не ответил вовремя.", "DIAGNOSTICS_AGENT_TIMEOUT");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ClusterLogger.LogWarning(ex, "Unable to start diagnostics on node {Node}", nodeId);
                return new ClusterDiagnosticsJobResult(nodeId, false, null, "failed", "Не удалось связаться с Node Agent.", "DIAGNOSTICS_AGENT_UNAVAILABLE");
            }
        });

        var jobs = await Task.WhenAll(tasks);
        foreach (var job in jobs)
        {
            if (job.Accepted && !string.IsNullOrWhiteSpace(job.JobId))
                TrackDiagnosticsArchiveJob(job.Node, job.JobId);
        }
        return new ClusterDiagnosticsBatchResult(jobs, mode, since, maxLogMb);
    }

    public async Task<JsonElement> GetDiagnosticsJobAsync(string nodeId, string jobId, CancellationToken ct)
    {
        var target = ResolveDiagnosticsTarget(nodeId, jobId);
        using var client = CreateDiagnosticsClient(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.BaseUrl}/ha/diagnostics/{Uri.EscapeDataString(jobId)}");
        request.Headers.TryAddWithoutValidation("X-TaskForge-Cluster-Token", target.Token);
        using var response = await client.SendAsync(request, timeout.Token);
        var raw = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw DiagnosticsAgentError(response.StatusCode, raw, "Не удалось получить состояние сборки логов.");
        using var doc = JsonDocument.Parse(raw);
        var result = doc.RootElement.Clone();
        TrackDiagnosticsArchiveJob(nodeId, jobId);
        return result;
    }

    public async Task ProxyDiagnosticsArchiveAsync(string nodeId, string jobId, HttpResponse output, CancellationToken ct)
    {
        var target = ResolveDiagnosticsTarget(nodeId, jobId);
        using var client = CreateDiagnosticsClient(target);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.BaseUrl}/ha/diagnostics/{Uri.EscapeDataString(jobId)}/archive");
        request.Headers.TryAddWithoutValidation("X-TaskForge-Cluster-Token", target.Token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw DiagnosticsAgentError(response.StatusCode, raw, "Архив диагностики пока недоступен.");
        }

        output.StatusCode = StatusCodes.Status200OK;
        output.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/gzip";
        if (response.Content.Headers.ContentLength is long length) output.ContentLength = length;
        var supplied = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        var safe = SafeArchiveName(supplied?.Trim('"') ?? $"taskforge_diagnostics_{nodeId}_{jobId}.tar.gz");
        output.Headers["Content-Disposition"] = new ContentDispositionHeaderValue("attachment") { FileNameStar = safe }.ToString();
        output.Headers.CacheControl = "no-store";
        Console.WriteLine($"[TFDIAG API DOWNLOAD] node={nodeId} job={jobId} file={safe} bytes={output.ContentLength?.ToString() ?? "unknown"} utc={DateTimeOffset.UtcNow:O}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await stream.CopyToAsync(output.Body, ct);
    }

    public async Task<ClusterDiagnosticsArchiveListResult> ListDiagnosticsArchivesAsync(CancellationToken ct)
    {
        try
        {
            var archives = await DiagnosticsArchiveStore.ListAsync(ct);
            return new ClusterDiagnosticsArchiveListResult(archives, DiagnosticsArchiveStore.RetentionDays, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ClusterLogger.LogWarning(ex, "Unable to list stored cluster diagnostics archives from MinIO.");
            throw new ClusterDiagnosticsException(StatusCodes.Status503ServiceUnavailable, "DIAGNOSTICS_ARCHIVE_STORAGE_UNAVAILABLE", "Хранилище сохранённых архивов диагностики временно недоступно.");
        }
    }

    public async Task ProxyStoredDiagnosticsArchiveAsync(string archiveId, HttpResponse output, CancellationToken ct)
    {
        try
        {
            await DiagnosticsArchiveStore.WriteDownloadAsync(archiveId, output, ct);
        }
        catch (ClusterDiagnosticsException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ClusterLogger.LogWarning(ex, "Unable to stream stored cluster diagnostics archive from MinIO.");
            throw new ClusterDiagnosticsException(StatusCodes.Status503ServiceUnavailable, "DIAGNOSTICS_ARCHIVE_STORAGE_UNAVAILABLE", "Сохранённый архив диагностики временно недоступен.");
        }
    }

    private void TrackDiagnosticsArchiveJob(string nodeId, string jobId)
    {
        ValidateNodeId(nodeId);
        ValidateJobId(jobId);
        var now = DateTimeOffset.UtcNow;
        _diagnosticsArchiveJobs.TryAdd(DiagnosticsArchiveJobKey(nodeId, jobId), new TrackedDiagnosticsArchiveJob(nodeId, jobId, now, now));
    }

    private static string DiagnosticsArchiveJobKey(string nodeId, string jobId) => nodeId + ":" + jobId;

    private async Task RunDiagnosticsArchiveLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PersistCompletedDiagnosticsArchivesAsync(stoppingToken);
                var now = DateTimeOffset.UtcNow;
                if (now - _lastDiagnosticsArchiveCleanupUtc >= TimeSpan.FromHours(1))
                {
                    // Throttle cleanup attempts as well as successful cleanups. A temporary
                    // MinIO outage must not turn the five-second persistence loop into log spam.
                    _lastDiagnosticsArchiveCleanupUtc = now;
                    await DiagnosticsArchiveStore.CleanupExpiredAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Diagnostics persistence is deliberately best-effort: MinIO or a
                // remote agent outage must never affect cluster telemetry/readiness.
                ClusterLogger.LogWarning(ex, "Cluster diagnostics archive persistence iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task PersistCompletedDiagnosticsArchivesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _diagnosticsArchiveJobs.ToArray())
        {
            var tracked = pair.Value;
            if (tracked.NextAttemptAt > now) continue;
            if (now - tracked.AcceptedAt > TimeSpan.FromDays(3))
            {
                _diagnosticsArchiveJobs.TryRemove(pair.Key, out _);
                continue;
            }

            try
            {
                var status = await GetDiagnosticsJobAsync(tracked.Node, tracked.JobId, ct);
                var state = status.TryGetProperty("status", out var stateValue) && stateValue.ValueKind == JsonValueKind.String
                    ? stateValue.GetString()?.Trim().ToLowerInvariant()
                    : null;
                if (state == "failed")
                {
                    _diagnosticsArchiveJobs.TryRemove(pair.Key, out _);
                    continue;
                }
                if (state != "completed")
                {
                    DelayDiagnosticsArchiveRetry(pair.Key, tracked, TimeSpan.FromSeconds(5));
                    continue;
                }

                await PersistDiagnosticsArchiveAsync(tracked.Node, tracked.JobId, ct);
                _diagnosticsArchiveJobs.TryRemove(pair.Key, out _);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DelayDiagnosticsArchiveRetry(pair.Key, tracked, TimeSpan.FromSeconds(30));
                ClusterLogger.LogWarning(ex, "Unable to persist diagnostics archive node={Node} job={JobId}; retry scheduled.", tracked.Node, tracked.JobId);
            }
        }
    }

    private void DelayDiagnosticsArchiveRetry(string key, TrackedDiagnosticsArchiveJob tracked, TimeSpan delay)
    {
        _diagnosticsArchiveJobs.TryUpdate(key, tracked with { NextAttemptAt = DateTimeOffset.UtcNow.Add(delay) }, tracked);
    }

    private async Task PersistDiagnosticsArchiveAsync(string nodeId, string jobId, CancellationToken ct)
    {
        var target = ResolveDiagnosticsTarget(nodeId, jobId);
        using var client = CreateDiagnosticsClient(target);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.BaseUrl}/ha/diagnostics/{Uri.EscapeDataString(jobId)}/archive");
        request.Headers.TryAddWithoutValidation("X-TaskForge-Cluster-Token", target.Token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw DiagnosticsAgentError(response.StatusCode, raw, "Архив диагностики пока недоступен для сохранения.");
        }

        var supplied = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        var safe = SafeArchiveName(supplied?.Trim('"') ?? $"taskforge_diagnostics_{nodeId}_{jobId}.tar.gz");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var stored = await DiagnosticsArchiveStore.SaveAsync(
            nodeId, jobId, safe, stream, response.Content.Headers.ContentType?.ToString(), response.Content.Headers.ContentLength, ct);
        Console.WriteLine($"[TFDIAG MINIO STORED] node={nodeId} job={jobId} archiveId={stored.Id} file={stored.FileName} bytes={stored.SizeBytes} expires={stored.ExpiresAt:O} utc={DateTimeOffset.UtcNow:O}");
    }

    private DiagnosticsTarget ResolveDiagnosticsTarget(string nodeId, string jobId)
    {
        ValidateNodeId(nodeId);
        ValidateJobId(jobId);
        var states = _agents.Values.Where(x => !x.NodeId.StartsWith("url:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var state = states.FirstOrDefault(x => string.Equals(x.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ClusterDiagnosticsException(StatusCodes.Status404NotFound, "DIAGNOSTICS_NODE_UNKNOWN", "Нода не найдена в текущей топологии.");
        var revision = AgentRevision(state);
        if (revision is null || revision < DiagnosticsMinAgentRevision)
            throw new ClusterDiagnosticsException(StatusCodes.Status409Conflict, "DIAGNOSTICS_AGENT_TOO_OLD", $"Node Agent r{revision?.ToString() ?? "?"} не поддерживает сбор логов. Требуется r{DiagnosticsMinAgentRevision}+.");
        return ResolveDiagnosticsTarget(state, states, DiagnosticsControlToken());
    }

    private DiagnosticsTarget ResolveDiagnosticsTarget(AgentState state, IEnumerable<AgentState> states, string token)
    {
        if (!string.IsNullOrWhiteSpace(_localNodeId) && string.Equals(state.NodeId, _localNodeId, StringComparison.OrdinalIgnoreCase))
        {
            var socketPath = LocalAgentSocketPath.Trim();
            if (socketPath.Length == 0 || !Path.IsPathRooted(socketPath))
                throw new ClusterDiagnosticsException(StatusCodes.Status503ServiceUnavailable, "DIAGNOSTICS_LOCAL_SOCKET_INVALID", "Локальный Unix socket Node Agent не настроен.");
            return new DiagnosticsTarget(state.NodeId, "http://localhost", token, socketPath);
        }

        var url = ResolveAgentUrl(state, states);
        if (string.IsNullOrWhiteSpace(url))
            throw new ClusterDiagnosticsException(StatusCodes.Status409Conflict, "DIAGNOSTICS_AGENT_URL_MISSING", "Не удалось определить адрес Node Agent.");
        return new DiagnosticsTarget(state.NodeId, url.TrimEnd('/'), token, null);
    }

    private HttpClient CreateDiagnosticsClient(DiagnosticsTarget target)
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

    private string DiagnosticsControlToken()
    {
        var key = ClusterConfiguration["ClusterTelemetry:InternalKey"]
            ?? ClusterConfiguration["InternalApi:Key"]
            ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY")
            ?? string.Empty;
        if (key.Length < 40)
            throw new ClusterDiagnosticsException(StatusCodes.Status503ServiceUnavailable, "DIAGNOSTICS_AUTH_NOT_CONFIGURED", "В observability-api не настроен внутренний ключ управления Node Agent.");
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(DiagnosticsTokenPurpose));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static int? AgentRevision(AgentState state)
    {
        var raw = ClusterTelemetryNormalizer.Text(state.Payload?["node"]?["bundle_revision"]);
        return int.TryParse(raw, out var revision) && revision >= 0 ? revision : null;
    }

    private static string? ResolveAgentUrl(AgentState state, IEnumerable<AgentState> states)
    {
        if (!string.Equals(state.Url, "local-file", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(state.Url, UriKind.Absolute, out _))
            return state.Url;
        foreach (var source in states)
        {
            if (source.Payload?["topology"] is not System.Text.Json.Nodes.JsonArray topology) continue;
            var meta = topology.OfType<System.Text.Json.Nodes.JsonObject>()
                .FirstOrDefault(x => string.Equals(ClusterTelemetryNormalizer.Text(x["id"]), state.NodeId, StringComparison.OrdinalIgnoreCase));
            var url = ClusterTelemetryNormalizer.Text(meta?["agent_url"]);
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _)) return url.TrimEnd('/');
            var ip = ClusterTelemetryNormalizer.Text(meta?["wireguard"]?["ip"]) ?? ClusterTelemetryNormalizer.Text(meta?["wireguard_ip"]);
            var port = ClusterTelemetryNormalizer.Number(meta?["health_port"]);
            var agentPort = port.HasValue && port.Value >= 1 && port.Value <= 65535 ? (int)port.Value : 9187;
            if (!string.IsNullOrWhiteSpace(ip)) return $"http://{ip}:{agentPort}";
        }
        return null;
    }

    private static string NormalizeDiagnosticsMode(string? mode)
    {
        var value = (mode ?? "standard").Trim().ToLowerInvariant();
        return value is "standard" or "quick" or "full"
            ? value
            : throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_MODE_INVALID", "Режим сбора логов должен быть standard, quick или full.");
    }

    private static string NormalizeDiagnosticsSince(string? since)
    {
        var value = (since ?? "6h").Trim().ToLowerInvariant();
        if (value.Length is < 2 or > 6 || value[0] == '0' || !char.IsDigit(value[0]) || !"smhdw".Contains(value[^1]) || value[..^1].Any(ch => !char.IsDigit(ch)))
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_SINCE_INVALID", "Период логов должен иметь вид 30m, 6h, 2d или 1w.");
        if (!int.TryParse(value[..^1], out var amount) || amount is < 1 or > 9999)
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_SINCE_INVALID", "Некорректный период логов.");
        return value;
    }

    private static void ValidateNodeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32 || value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_NODE_INVALID", "Некорректный идентификатор ноды.");
    }

    private static void ValidateJobId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_JOB_INVALID", "Некорректный идентификатор сборки логов.");
    }

    private static ClusterDiagnosticsException DiagnosticsAgentError(HttpStatusCode status, string raw, string fallback)
    {
        var payload = TryParseJson(raw);
        var message = ReadString(payload, "message") ?? ReadString(payload, "error") ?? fallback;
        var code = ReadString(payload, "code") ?? "DIAGNOSTICS_AGENT_ERROR";
        var publicStatus = status switch
        {
            HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
            HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
            HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status502BadGateway,
        };
        return new ClusterDiagnosticsException(publicStatus, code, message);
    }

    private static Dictionary<string, JsonElement>? TryParseJson(string raw)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw, DiagnosticsJson); }
        catch { return null; }
    }

    private static string? ReadString(Dictionary<string, JsonElement>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string SafeArchiveName(string value)
    {
        var cleaned = new string(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "taskforge_diagnostics.tar.gz" : cleaned;
    }
}
