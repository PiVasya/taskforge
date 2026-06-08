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

app.UseTaskForgeRequestSecurity("execution");

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-execution-api" }));
app.MapGet("/health/ready", async (ExecutionDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-execution-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-execution-api", database = "taskforge_execution", status = "execution microservice active" }));
app.MapGet("/api/execution/api/schema-owner", () => Results.Ok(new { database = "taskforge_execution", ownedEntities = new[] { "ExecutionJob", "ExecutionResult", "RunnerHeartbeat" } }));

app.MapPost("/api/compiler/compile-run", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: false));
app.MapPost("/api/compiler/run-tests", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: true));
app.MapPost("/api/internal/execution/compile-run", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: false));
app.MapPost("/api/internal/execution/run-tests", async (RunnerRequest request, IHttpClientFactory factory) => await ProxyRunAsync(request, factory, tests: true));

app.MapPost("/api/internal/execution/jobs", async (CreateExecutionJobRequest request, ExecutionDbContext db, CancellationToken ct) =>
{
    if (request.SubmissionId == Guid.Empty) return Results.BadRequest(new { message = "submissionId is required", code = "SUBMISSION_ID_REQUIRED" });
    if (string.IsNullOrWhiteSpace(request.Code)) return Results.BadRequest(new { message = "code is required", code = "CODE_REQUIRED" });

    var job = new ExecutionJob
    {
        SubmissionId = request.SubmissionId,
        AssignmentId = request.AssignmentId,
        UserId = request.UserId,
        Language = NormalizeLanguage(request.Language),
        Code = request.Code ?? string.Empty,
        Input = request.Input,
        TestsJson = request.Tests.HasValue ? request.Tests.Value.GetRawText() : request.TestCases.HasValue ? request.TestCases.Value.GetRawText() : request.TestsJson,
        CodeForbiddenCallsJson = request.CodeForbiddenCalls.HasValue ? request.CodeForbiddenCalls.Value.GetRawText() : StringArrayJson(request.PolicyForbiddenCalls),
        CodeRequiredCallsJson = request.CodeRequiredCalls.HasValue ? request.CodeRequiredCalls.Value.GetRawText() : StringArrayJson(request.PolicyRequiredCalls),
        TimeLimitMs = request.TimeLimitMs,
        MemoryLimitMb = request.MemoryLimitMb,
        Status = "queued"
    };
    db.ExecutionJobs.Add(job);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToJobDto(job));
});

app.MapPost("/api/internal/execution/jobs/claim-next", async (ExecutionDbContext db, IConfiguration cfg, CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    var maxAttempts = Math.Clamp(cfg.GetValue("ExecutionQueue:MaxAttempts", 3), 1, 10);
    var runningTimeoutMinutes = Math.Clamp(cfg.GetValue("ExecutionQueue:RunningTimeoutMinutes", 5), 1, 60);
    var staleBefore = now.AddMinutes(-runningTimeoutMinutes);

    await db.ExecutionJobs
        .Where(x => x.Status == "running" && x.StartedAt != null && x.StartedAt < staleBefore && x.AttemptCount < maxAttempts)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, "queued")
            .SetProperty(x => x.StartedAt, (DateTimeOffset?)null), ct);

    while (true)
    {
        var candidateId = await db.ExecutionJobs
            .Where(x => x.Status == "queued" && x.AttemptCount < maxAttempts)
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (candidateId == Guid.Empty) return Results.Ok(new { job = (object?)null });

        var claimed = await db.ExecutionJobs
            .Where(x => x.Id == candidateId && x.Status == "queued")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "running")
                .SetProperty(x => x.StartedAt, now)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), ct);

        if (claimed == 0) continue;

        var job = await db.ExecutionJobs.AsNoTracking().FirstAsync(x => x.Id == candidateId, ct);
        return Results.Ok(new { job = ToJobDto(job) });
    }
});

app.MapPost("/api/internal/execution/jobs/{jobId:guid}/complete", async (Guid jobId, CompleteExecutionJobRequest request, ExecutionDbContext db, CancellationToken ct) =>
{
    var job = await db.ExecutionJobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
    if (job == null) return Results.NotFound(new { message = "Execution job not found", code = "EXECUTION_JOB_NOT_FOUND" });

    job.Status = string.IsNullOrWhiteSpace(request.Status) ? "completed" : request.Status.Trim();
    job.CompletedAt = DateTimeOffset.UtcNow;

    var result = await db.ExecutionResults.FirstOrDefaultAsync(x => x.JobId == jobId, ct);
    if (result == null)
    {
        result = new ExecutionResult { JobId = jobId };
        db.ExecutionResults.Add(result);
    }

    result.Status = job.Status;
    result.Stdout = request.Stdout;
    result.Stderr = request.Stderr;
    result.ExitCode = request.ExitCode;
    result.Score = Math.Clamp(request.Score, 0, 100);
    result.Passed = request.Passed;
    result.DurationMs = Math.Max(0, request.DurationMs);
    result.ResultJson = request.Result.HasValue ? request.Result.Value.GetRawText() : null;

    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job = ToJobDto(job), result });
});


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
    "c++" or "cpp" or "g++" or "gcc" or "cxx" => "cpp",
    "py" or "python" or "python3" => "python",
    "js" or "javascript" or "node" or "nodejs" or "node.js" => "javascript",
    "java" => "java",
    "pascal" or "pabc" => "pascal",
    var x => x
};
static string? StringArrayJson(IEnumerable<string>? values)
{
    if (values == null) return null;
    var list = values.Select(x => (x ?? string.Empty).Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    return list.Length == 0 ? null : JsonSerializer.Serialize(list);
}

static string? RunnerService(string lang, bool image) => (lang, image) switch
{
    ("csharp", false) => "csharp-runner",
    ("cpp", false) => "cpp-runner",
    ("python", false) => "python-runner",
    ("java", false) => "java-runner",
    ("javascript", false) => "javascript-runner",
    ("pascal", false) => "pascal-runner",
    ("cpp", true) => "image-cpp-runner",
    ("pascal", true) => "image-pascal-runner",
    _ => null
};

static object ToJobDto(ExecutionJob x) => new { x.Id, x.SubmissionId, x.AssignmentId, x.UserId, x.Language, x.Code, x.Input, x.TestsJson, x.CodeForbiddenCallsJson, x.CodeRequiredCallsJson, x.TimeLimitMs, x.MemoryLimitMb, x.AttemptCount, x.Status, x.CreatedAt, x.StartedAt, x.CompletedAt };

public sealed record RunnerRequest(string? Language, string? Code, string? Input, JsonElement[]? TestCases, JsonElement[]? Tests, int? TimeLimitMs, int? MemoryLimitMb);
public sealed record CreateExecutionJobRequest(Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, JsonElement? TestCases, JsonElement? Tests, string? TestsJson, int? TimeLimitMs, int? MemoryLimitMb, JsonElement? CodeForbiddenCalls, JsonElement? CodeRequiredCalls, List<string>? PolicyForbiddenCalls, List<string>? PolicyRequiredCalls);
public sealed record CompleteExecutionJobRequest(string? Status, string? Stdout, string? Stderr, int? ExitCode, long DurationMs, int Score, bool Passed, JsonElement? Result);
