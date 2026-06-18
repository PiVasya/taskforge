using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

using TaskForge.Ai.Api.Contracts;
using TaskForge.Ai.Api.Hubs;
using static TaskForge.Ai.Api.Services.Access.AiApiAccessService;
using static TaskForge.Ai.Api.Services.Common.AiApiCommonService;
using static TaskForge.Ai.Api.Services.Mapping.AiApiMappingService;
using static TaskForge.Ai.Api.Services.Results.AiApiResultsService;
using static TaskForge.Ai.Api.Services.Serialization.AiApiSerializationService;

namespace TaskForge.Ai.Api.Services.Testing;

internal static class AiApiTestingService
{
    internal static async Task<IResult> RunTestsBridge(AgentRunTestsRequest request, IHttpClientFactory factory, IConfiguration cfg, CancellationToken ct)
    {
        var testCases = request.TestCases.HasValue && request.TestCases.Value.ValueKind == JsonValueKind.Array
            ? request.TestCases.Value.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "http://execution-api:8080/api/internal/execution/run-tests")
        {
            Content = JsonContent.Create(new { language = request.Language ?? "cpp", code = request.Code ?? string.Empty, testCases }, options: JsonOptions())
        };
        AddInternalKey(msg, cfg);
        var response = await client.SendAsync(msg, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return Microsoft.AspNetCore.Http.Results.Content(raw, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
    }

}
