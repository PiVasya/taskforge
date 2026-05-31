using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<TasksDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<TasksDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for TasksDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for TasksDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<TasksDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-tasks-api" }));
app.MapGet("/health/ready", async (TasksDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-tasks-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-tasks-api", database = "taskforge_tasks", status = "tasks microservice active" }));
app.MapGet("/api/tasks/assignment-api/schema-owner", () => Results.Ok(new { database = "taskforge_tasks", ownedEntities = new[] { "Assignment", "TaskTest", "MathTask" } }));

app.MapGet("/api/courses/{courseId:guid}/assignments", async (Guid courseId, TasksDbContext db) =>
{
    var rows = await db.Assignments.AsNoTracking().Where(x => x.CourseId == courseId).OrderBy(x => x.Sort).ThenBy(x => x.CreatedAt).ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});

app.MapPost("/api/courses/{courseId:guid}/assignments", async (Guid courseId, AssignmentRequest request, TasksDbContext db) =>
{
    var maxSort = await db.Assignments.Where(x => x.CourseId == courseId).Select(x => (int?)x.Sort).MaxAsync() ?? -1;
    var assignment = new Assignment
    {
        CourseId = courseId,
        Title = string.IsNullOrWhiteSpace(request.Title) ? "Новое задание" : request.Title.Trim(),
        Description = request.Description,
        Type = string.IsNullOrWhiteSpace(request.Type) ? "code-test" : request.Type.Trim(),
        Language = string.IsNullOrWhiteSpace(request.Language) ? "csharp" : request.Language.Trim(),
        StarterCode = request.StarterCode,
        TestsJson = request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases),
        IsVisible = request.IsVisible ?? !(request.IsHidden ?? false),
        Sort = maxSort + 1
    };
    db.Assignments.Add(assignment);
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment));
});

app.MapGet("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, TasksDbContext db) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    return assignment == null ? Results.NotFound() : Results.Ok(ToDto(assignment));
});

app.MapPut("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, AssignmentRequest request, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    if (!string.IsNullOrWhiteSpace(request.Title)) assignment.Title = request.Title.Trim();
    assignment.Description = request.Description ?? assignment.Description;
    if (!string.IsNullOrWhiteSpace(request.Type)) assignment.Type = request.Type.Trim();
    if (!string.IsNullOrWhiteSpace(request.Language)) assignment.Language = request.Language.Trim();
    if (request.StarterCode != null) assignment.StarterCode = request.StarterCode;
    if (request.TestsJson != null || request.Tests.HasValue) assignment.TestsJson = request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases);
    if (request.IsVisible.HasValue) assignment.IsVisible = request.IsVisible.Value;
    if (request.IsHidden.HasValue) assignment.IsVisible = !request.IsHidden.Value;
    assignment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment));
});

app.MapDelete("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    db.Assignments.Remove(assignment);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "deleted" });
});

app.MapPatch("/api/assignments/{assignmentId:guid}/sort", async (Guid assignmentId, SortRequest request, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    assignment.Sort = request.Sort;
    assignment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment));
});

app.MapPatch("/api/assignments/{assignmentId:guid}/position", async (Guid assignmentId, PositionRequest request, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    var siblings = await db.Assignments.Where(x => x.CourseId == assignment.CourseId && x.Id != assignment.Id).OrderBy(x => x.Sort).ToListAsync();
    var pos = Math.Clamp((request.Position ?? siblings.Count + 1) - 1, 0, siblings.Count);
    siblings.Insert(pos, assignment);
    for (var i = 0; i < siblings.Count; i++) siblings[i].Sort = i;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment));
});

app.MapPatch("/api/assignments/{assignmentId:guid}/visibility", async (Guid assignmentId, VisibilityRequest request, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    assignment.IsVisible = request.IsVisible;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment));
});

app.MapGet("/api/task-tests/{assignmentId:guid}/edit", async (Guid assignmentId, TasksDbContext db) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    return assignment == null ? Results.NotFound() : Results.Ok(new { assignmentId, tests = ParseJson(assignment.TestsJson), title = assignment.Title });
});
app.MapPut("/api/task-tests/{assignmentId:guid}/edit", async (Guid assignmentId, JsonElement payload, TasksDbContext db) => await SaveExtraJson(assignmentId, payload, db));
app.MapPost("/api/task-tests/{assignmentId:guid}/start", (Guid assignmentId) => Results.Ok(new { attemptId = Guid.NewGuid(), assignmentId, startedAt = DateTimeOffset.UtcNow }));
app.MapPost("/api/task-tests/{assignmentId:guid}/submit", (Guid assignmentId, JsonElement payload) => Results.Ok(new { attemptId = Guid.NewGuid(), assignmentId, passed = true, score = 100, submittedAt = DateTimeOffset.UtcNow }));

app.MapGet("/api/math-tasks/{assignmentId:guid}/edit", async (Guid assignmentId, TasksDbContext db) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    return assignment == null ? Results.NotFound() : Results.Ok(new { assignmentId, data = ParseJson(assignment.TestsJson), title = assignment.Title });
});
app.MapPut("/api/math-tasks/{assignmentId:guid}/edit", async (Guid assignmentId, JsonElement payload, TasksDbContext db) => await SaveExtraJson(assignmentId, payload, db));
app.MapPost("/api/math-tasks/{assignmentId:guid}/start", (Guid assignmentId) => Results.Ok(new { attemptId = Guid.NewGuid(), assignmentId, startedAt = DateTimeOffset.UtcNow }));
app.MapPost("/api/math-tasks/{assignmentId:guid}/submit", (Guid assignmentId, JsonElement payload) => Results.Ok(new { attemptId = Guid.NewGuid(), assignmentId, passed = true, score = 100, submittedAt = DateTimeOffset.UtcNow }));

app.MapGet("/api/me/test-attempts", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/me/test-attempts/{attemptId:guid}", (Guid attemptId) => Results.Ok(new { id = attemptId }));
app.MapGet("/api/me/math-attempts", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/me/math-attempts/{attemptId:guid}", (Guid attemptId) => Results.Ok(new { id = attemptId }));
app.MapGet("/api/admin/test-attempts/{attemptId:guid}", (Guid attemptId) => Results.Ok(new { id = attemptId }));
app.MapGet("/api/admin/math-attempts/{attemptId:guid}", (Guid attemptId) => Results.Ok(new { id = attemptId }));
app.MapDelete("/api/admin/test-attempts/{attemptId:guid}", (Guid attemptId) => Results.Ok(new { deleted = attemptId }));
app.MapDelete("/api/admin/math-attempts/{attemptId:guid}", (Guid attemptId) => Results.Ok(new { deleted = attemptId }));
app.MapGet("/api/admin/assignments/{assignmentId:guid}/insights", (Guid assignmentId) => Results.Ok(new { assignmentId, attempts = 0, solved = 0, averageScore = 0 }));
app.MapGet("/api/admin/users/{userId:guid}/test-attempts", (Guid userId) => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/admin/users/{userId:guid}/math-attempts", (Guid userId) => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/admin/users/{userId:guid}/groups", (Guid userId) => Results.Ok(Array.Empty<object>()));

app.Run();

static object ToDto(Assignment x) => new
{
    x.Id,
    x.CourseId,
    x.Title,
    x.Description,
    x.Type,
    x.Language,
    allowedLanguages = new[] { x.Language },
    x.StarterCode,
    tests = ParseJson(x.TestsJson),
    testCases = ParseJson(x.TestsJson),
    tags = string.Empty,
    difficulty = 1,
    rating = 1,
    isHidden = !x.IsVisible,
    isAiDraft = false,
    lifecycleStatus = x.IsVisible ? "published" : "draft",
    codeForbiddenCalls = Array.Empty<string>(),
    codeRequiredCalls = Array.Empty<string>(),
    imageTestReferenceKey = (string?)null,
    imageTestSimilarityThreshold = 90,
    x.IsVisible,
    x.Sort,
    canEdit = true,
    isSolved = false,
    x.CreatedAt,
    x.UpdatedAt
};
static string? RawJson(JsonElement? value) => value.HasValue ? value.Value.GetRawText() : null;
static object? ParseJson(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return json; }
}
static async Task<IResult> SaveExtraJson(Guid assignmentId, JsonElement payload, TasksDbContext db)
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    assignment.TestsJson = payload.GetRawText();
    assignment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment));
}

public sealed record AssignmentRequest(string? Title, string? Description, string? Type, string? Language, string? StarterCode, string? TestsJson, JsonElement? Tests, JsonElement? TestCases, bool? IsVisible, bool? IsHidden);
public sealed record SortRequest(int Sort);
public sealed record PositionRequest(int? Position, Guid? AfterAssignmentId);
public sealed record VisibilityRequest(bool IsVisible);
