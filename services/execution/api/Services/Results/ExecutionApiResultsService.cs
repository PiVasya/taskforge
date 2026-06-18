using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

using TaskForge.Execution.Api.Contracts;
using static TaskForge.Execution.Api.Services.Mapping.ExecutionApiMappingService;
using static TaskForge.Execution.Api.Services.Serialization.ExecutionApiSerializationService;

namespace TaskForge.Execution.Api.Services.Results;

internal static class ExecutionApiResultsService
{
    internal static async Task<IResult> ProxyRunAsync(RunnerRequest request, IHttpClientFactory factory, bool tests, bool image = false)
    {
        var language = NormalizeLanguage(request.Language);
        var service = RunnerService(language, image);
        var port = image ? 8000 : 8080;
        if (service == null) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = $"Unsupported language: {request.Language}" });

        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(40);
        var url = $"http://{service}:{port}" + (tests ? "/run-tests" : "/run");
        object payload = tests
            ? new { code = request.Code ?? string.Empty, tests = request.TestCases ?? request.Tests ?? Array.Empty<JsonElement>(), timeLimitMs = request.TimeLimitMs, memoryLimitMb = request.MemoryLimitMb }
            : new { code = request.Code ?? string.Empty, input = request.Input, timeLimitMs = request.TimeLimitMs, memoryLimitMb = request.MemoryLimitMb };

        try
        {
            using var response = await client.PostAsJsonAsync(url, payload);
            var text = await response.Content.ReadAsStringAsync();
            return Microsoft.AspNetCore.Http.Results.Content(text, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            return Microsoft.AspNetCore.Http.Results.Json(new { status = "runner_unavailable", message = ex.Message, runner = service }, statusCode: 503);
        }
    }

    internal static string? RunnerService(string lang, bool image) => (lang, image) switch
    {
        ("csharp", false) => "csharp-runner",
        ("cpp", false) => "cpp-runner",
        ("python", false) => "python-runner",
        ("java", false) => "java-runner",
        ("javascript", false) => "javascript-runner",
        ("pascal", false) => "pascal-runner",
        ("cpp", true) => "image-cpp-runner",
        ("pascal", true) => "image-pascal-runner",
        ("python", true) => "image-python-runner",
        _ => null
    };

}
