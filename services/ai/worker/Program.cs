using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Infrastructure;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("ai-worker");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "ai-worker");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddOptions<TaskForgeInternalApiOptions>()
    .Bind(builder.Configuration.GetSection("TaskForgeInternalApi"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<TaskForgeInternalApiClient>();
builder.Services.AddSingleton<TaskForgeAgentWakeService>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TaskForgeAgentWakeService>());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    mode = "backend-push-stub",
    utc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/internal/wake", (
    HttpContext http,
    JsonElement payload,
    IOptions<TaskForgeInternalApiOptions> options,
    TaskForgeAgentWakeService worker) =>
{
    var expected = options.Value.ApiKey ?? string.Empty;
    var supplied = http.Request.Headers["X-Internal-Key"].ToString();
    if (string.IsNullOrWhiteSpace(expected) || !FixedTimeEquals(expected, supplied))
    {
        return Results.Json(new { message = "Неверный внутренний ключ.", code = "INTERNAL_KEY_INVALID" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var reason = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("reason", out var reasonNode)
        ? reasonNode.GetString()
        : null;
    var runId = payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("runId", out var runIdNode)
        && Guid.TryParse(runIdNode.ToString(), out var parsedRunId)
            ? parsedRunId
            : (Guid?)null;
    var accepted = worker.Wake(reason ?? "backend-request", runId);
    return Results.Accepted((string?)null, new
    {
        ok = true,
        accepted,
        mode = "backend-push-stub"
    });
});

app.Run();

static bool FixedTimeEquals(string expected, string supplied)
{
    var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    var right = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
    return CryptographicOperations.FixedTimeEquals(left, right);
}
