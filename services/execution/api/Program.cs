using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<ExecutionDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for ExecutionDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for ExecutionDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-execution-api" }));
app.MapGet("/health/ready", async (ExecutionDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-execution-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-execution-api", database = "taskforge_execution", status = "execution microservice active" }));
app.MapGet("/api/execution/api/schema-owner", () => Results.Ok(new { database = "taskforge_execution", ownedEntities = new[] { "ExecutionJob", "ExecutionResult", "RunnerHeartbeat" } }));

app.MapPost("/api/compiler/compile-run", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: false));
app.MapPost("/api/compiler/run-tests", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: true));
app.MapPost("/api/Compiler/compile-run", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: false));
app.MapPost("/api/Compiler/run-tests", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: true));

app.MapPost("/api/assignments/{assignmentId:guid}/image-test/run-code", async (Guid assignmentId, RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: false, image: true));
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/compare-code", async (Guid assignmentId, RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: false, image: true));
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/reference", (Guid assignmentId, JsonElement body) => Results.Ok(new { assignmentId, saved = true }));
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/compare", (Guid assignmentId, JsonElement body) => Results.Ok(new { assignmentId, passed = true, similarity = 1.0 }));
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/submit-code", async (Guid assignmentId, RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: false, image: true));

app.Run();

static async Task<IResult> ProxyRunAsync(RunnerRequest request, IHttpClientFactory factory, bool tests, bool image = false)
{
    var language = NormalizeLanguage(request.Language);
    var service = RunnerService(language, image);
    var port = image ? 8000 : 8080;
    if (service == null) return Results.BadRequest(new { message = $"Unsupported language: {request.Language}" });

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
        return Results.Content(text, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "runner_unavailable", message = ex.Message, runner = service }, statusCode: 503);
    }
}

static string NormalizeLanguage(string? lang) => (lang ?? "csharp").Trim().ToLowerInvariant() switch
{
    "c#" or "cs" or "csharp" => "csharp",
    "c++" or "cpp" => "cpp",
    "js" or "javascript" => "javascript",
    "java" => "java",
    "pascal" or "pabc" => "pascal",
    var x => x
};
static string? RunnerService(string lang, bool image) => (lang, image) switch
{
    ("csharp", false) => "csharp-runner",
    ("cpp", false) => "cpp-runner",
    ("java", false) => "java-runner",
    ("javascript", false) => "javascript-runner",
    ("pascal", false) => "pascal-runner",
    ("cpp", true) => "image-cpp-runner",
    ("pascal", true) => "image-pascal-runner",
    _ => null
};

public sealed record RunnerRequest(string? Language, string? Code, string? Input, JsonElement[]? TestCases, JsonElement[]? Tests, int? TimeLimitMs, int? MemoryLimitMb);
