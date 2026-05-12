using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Options;

namespace TaskForge.AiAgent.Infrastructure;

public sealed class TaskForgeInternalApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _http;
    private readonly TaskForgeInternalApiOptions _options;
    private readonly ILogger<TaskForgeInternalApiClient> _logger;

    public TaskForgeInternalApiClient(HttpClient http, IOptions<TaskForgeInternalApiOptions> options, ILogger<TaskForgeInternalApiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Remove("X-Internal-Key");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Internal-Key", _options.ApiKey);
        }
    }

    public async Task<ClaimedAgentJob?> ClaimNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var response = await PostJsonAsync("api/internal/agent/claim-next", new { workerId }, cancellationToken);
        if (response?["job"] is not JsonObject && response?["job"] is not JsonValue && response?["job"] is not JsonArray)
            return null;

        var jobNode = response["job"];
        if (jobNode is null || jobNode.GetValueKind() == JsonValueKind.Null)
            return null;

        using var doc = JsonDocument.Parse(jobNode.ToJsonString(JsonOptions));
        return ClaimedAgentJob.FromJobElement(doc.RootElement);
    }

    public async Task HeartbeatAsync(Guid runId, string workerId, CancellationToken cancellationToken)
        => await PostJsonAsync($"api/internal/agent/runs/{runId}/heartbeat", new { workerId }, cancellationToken);

    public async Task AppendStepAsync(Guid runId, string workerId, AgentStepPayload step, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["workerId"] = workerId,
            ["step"] = new JsonObject
            {
                ["kind"] = step.Kind,
                ["status"] = step.Status,
                ["actionName"] = step.ActionName,
                ["title"] = step.Title,
                ["summary"] = step.Summary,
                ["data"] = step.Data?.DeepClone()
            }
        };
        await PostJsonAsync($"api/internal/agent/runs/{runId}/steps", payload, cancellationToken);
    }

    public async Task CompleteAsync(Guid runId, string workerId, AgentResultEnvelope result, bool includeDebug, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["workerId"] = workerId,
            ["result"] = result.ToJsonObject(includeDebug)
        };
        await PostJsonAsync($"api/internal/agent/runs/{runId}/complete", payload, cancellationToken);
    }

    public async Task FailAsync(Guid runId, string workerId, Exception exception, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["workerId"] = workerId,
            ["error"] = new JsonObject
            {
                ["message"] = exception.Message,
                ["type"] = exception.GetType().Name,
                ["stackTrace"] = exception.StackTrace
            }
        };
        await PostJsonAsync($"api/internal/agent/runs/{runId}/fail", payload, cancellationToken);
    }

    public async Task<JsonObject> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken)
    {
        if (request.Tests.Count == 0)
            throw new InvalidOperationException("RunTestsAsync requires at least one test case. Empty test lists hide validation failures.");
        if (!request.RunId.HasValue)
            throw new InvalidOperationException("RunTestsAsync requires runId so backend can verify the worker lease.");
        if (string.IsNullOrWhiteSpace(request.WorkerId))
            throw new InvalidOperationException("RunTestsAsync requires workerId so backend can verify the worker lease.");

        var body = new JsonObject
        {
            ["runId"] = request.RunId?.ToString(),
            ["workerId"] = request.WorkerId,
            ["language"] = request.Language,
            ["code"] = request.Code,
            ["testCases"] = new JsonArray(request.Tests.Select(t => new JsonObject
            {
                ["input"] = t.Input,
                ["expectedOutput"] = t.ExpectedOutput,
                ["isHidden"] = t.IsHidden
            }).ToArray<JsonNode?>())
        };
        return await PostJsonAsync("api/internal/agent/tools/run-tests", body, cancellationToken) ?? new JsonObject { ["ok"] = false };
    }

    private async Task<JsonObject?> PostJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("TaskForge internal API call failed: {Path} {Status} {Body}", path, response.StatusCode, raw);
            throw new HttpRequestException(
                $"TaskForge internal API call failed: {(int)response.StatusCode} ({response.StatusCode}) for {path}. Response: {raw}",
                null,
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(raw))
            return new JsonObject { ["ok"] = true };

        try
        {
            return JsonNode.Parse(raw)?.AsObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"TaskForge internal API returned invalid JSON for {path}: {raw}", ex);
        }
    }
}
