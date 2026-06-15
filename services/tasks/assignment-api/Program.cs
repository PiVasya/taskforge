using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("tasks-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "tasks-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<TasksDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("tasks-api");

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
    await ensureScope.ServiceProvider.GetRequiredService<TasksDbContext>().Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("tasks");

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-tasks-api" }));
app.MapGet("/health/ready", async (TasksDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-tasks-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-tasks-api", database = "taskforge_tasks", status = "tasks microservice active" }));
app.MapGet("/api/tasks/assignment-api/schema-owner", () => Results.Ok(new { database = "taskforge_tasks", ownedEntities = new[] { "Assignment", "TaskAttempt" } }));

app.MapGet("/api/courses/{courseId:guid}/assignments", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
{
    var includeHidden = IsEditor(http, cfg);
    if (!includeHidden && !await CanUserAccessCourseAsync(courseId, http, cfg, clients, ct))
    {
        return Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });
    }

    var query = db.Assignments.AsNoTracking().Where(x => x.CourseId == courseId);
    if (!includeHidden) query = query.Where(x => x.IsVisible);
    var rows = await query.OrderBy(x => x.Sort).ThenBy(x => x.CreatedAt).ToListAsync(ct);
    return Results.Ok(rows.Select(x => ToDto(x, includeHidden)).ToList());
});

app.MapPost("/api/courses/{courseId:guid}/assignments", async (Guid courseId, AssignmentRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
{
    var maxSort = await db.Assignments.Where(x => x.CourseId == courseId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1;
    var assignment = await BuildAssignmentEntityAsync(courseId, request, maxSort + 1, clients, cfg, ct);
    db.Assignments.Add(assignment);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToDto(assignment, includeSensitive: true));
});

app.MapPost("/api/courses/{courseId:guid}/assignments/import-json", async (Guid courseId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (!IsEditor(http, cfg))
    {
        return Results.Json(new { message = "Для импорта заданий нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
    }

    var sourceItems = ExtractAssignmentImportItems(payload).ToList();
    if (sourceItems.Count == 0)
    {
        return Results.Json(new { message = "JSON не содержит заданий. Передай объект задания, массив заданий или объект с полем assignments/items/tasks.", code = "IMPORT_EMPTY" }, statusCode: StatusCodes.Status400BadRequest);
    }
    if (sourceItems.Count > 200)
    {
        return Results.Json(new { message = "За один импорт можно создать не больше 200 заданий.", code = "IMPORT_TOO_LARGE", count = sourceItems.Count }, statusCode: StatusCodes.Status400BadRequest);
    }

    var requests = new List<AssignmentRequest>();
    var issues = new List<object>();
    for (var i = 0; i < sourceItems.Count; i++)
    {
        try
        {
            var req = AssignmentRequestFromJson(sourceItems[i]);
            var itemIssues = ValidateImportedAssignment(req, i + 1).ToList();
            if (itemIssues.Count > 0)
            {
                issues.Add(new { index = i + 1, title = req.Title, issues = itemIssues });
            }
            requests.Add(req);
        }
        catch (Exception ex)
        {
            issues.Add(new { index = i + 1, issues = new[] { $"Не удалось прочитать объект задания: {ex.Message}" } });
        }
    }

    if (issues.Count > 0)
    {
        return Results.Json(new { message = "Импорт остановлен: в JSON есть ошибки.", code = "IMPORT_VALIDATION_FAILED", issues }, statusCode: StatusCodes.Status400BadRequest);
    }

    var maxSort = await db.Assignments.Where(x => x.CourseId == courseId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1;
    var created = new List<Assignment>();
    for (var i = 0; i < requests.Count; i++)
    {
        created.Add(await BuildAssignmentEntityAsync(courseId, requests[i], maxSort + 1 + i, clients, cfg, ct));
    }

    db.Assignments.AddRange(created);
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        createdCount = created.Count,
        assignments = created.Select(x => ToDto(x, includeSensitive: true)).ToList()
    });
});

app.MapGet("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var includeSensitive = IsEditor(http, cfg);
    if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, ct)) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    return Results.Ok(ToDto(assignment, includeSensitive));
});

app.MapGet("/api/assignments/{assignmentId:guid}/edit", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db) =>
{
    if (!IsEditor(http, cfg)) return Results.Json(new { message = "Для редактирования нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    return assignment == null ? Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" }) : Results.Ok(ToDto(assignment, includeSensitive: true));
});

app.MapGet("/api/internal/assignments/{assignmentId:guid}/judge-spec", async (Guid assignmentId, TasksDbContext db) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    if (assignment == null)
    {
        return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    }

    return Results.Ok(new
    {
        assignment.Id,
        assignment.Type,
        assignment.Language,
        allowedLanguages = ParseCsv(assignment.AllowedLanguagesCsv, assignment.Language),
        codeForbiddenCalls = ParseStringArrayJson(assignment.CodeForbiddenCallsJson),
        codeRequiredCalls = ParseStringArrayJson(assignment.CodeRequiredCallsJson),
        tests = ParseJson(assignment.TestsJson),
        testCases = ParseJson(assignment.TestsJson),
        testsJson = assignment.TestsJson
    });
});

app.MapGet("/api/internal/assignments/{assignmentId:guid}/access/{userId:guid}", async (Guid assignmentId, Guid userId, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

    var courseAccess = await LoadCourseAccessAsync(assignment.CourseId, userId, clients, cfg, ct);
    var canView = assignment.IsVisible && courseAccess?.CanView == true;
    return Results.Ok(new
    {
        assignmentId,
        assignment.CourseId,
        userId,
        canView,
        canSubmit = canView,
        assignment.IsVisible,
        canEdit = courseAccess?.CanEdit == true
    });
});

app.MapPost("/api/internal/assignments/summaries", async (AssignmentIdsRequest request, TasksDbContext db, IDistributedCache cache, IConfiguration cfg, ILogger<Program> logger, CancellationToken ct) =>
{
    var ids = (request.AssignmentIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(2000).OrderBy(x => x).ToArray();
    if (ids.Length == 0) return Results.Ok(Array.Empty<AssignmentSummaryDto>());

    var key = TaskForgeCache.Key("tasks:assignment-summaries:v2", ids);
    var rows = await TaskForgeCache.GetOrSetAsync(cache, cfg, logger, key, TaskForgeCache.Ttl(cfg, "Metadata", 300), async token =>
    {
        var assignments = await db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(token);
        return assignments.Select(ToAssignmentSummaryDto).ToList();
    }, ct);
    return Results.Ok(rows);
});

app.MapGet("/api/internal/users/{userId:guid}/activity-summary", async (Guid userId, TasksDbContext db, CancellationToken ct) =>
{
    var testAttempts = await db.Attempts.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "test").ToListAsync(ct);
    var mathAttempts = await db.Attempts.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "math").ToListAsync(ct);
    var solved = testAttempts.Where(x => x.Passed).Select(x => x.TaskAssignmentId)
        .Concat(mathAttempts.Where(x => x.Passed).Select(x => x.TaskAssignmentId))
        .Distinct()
        .Count();
    return Results.Ok(new
    {
        solvedAssignments = solved,
        totalAttempts = testAttempts.Count + mathAttempts.Count,
        codeSolutions = 0,
        imageSolutions = 0,
        testAttempts = testAttempts.Count,
        mathAttempts = mathAttempts.Count
    });
});

app.MapPost("/api/internal/activity/leaderboard", async (ActivityLeaderboardRequest request, TasksDbContext db, CancellationToken ct) =>
{
    var since = request.Days.HasValue && request.Days.Value > 0 ? DateTimeOffset.UtcNow.AddDays(-request.Days.Value) : (DateTimeOffset?)null;
    var userFilter = (request.UserIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToHashSet();
    var q = db.Attempts.AsNoTracking().Where(x => x.Passed);
    if (since.HasValue) q = q.Where(x => x.SubmittedAt.HasValue && x.SubmittedAt.Value >= since.Value);
    if (userFilter.Count > 0) q = q.Where(x => userFilter.Contains(x.UserId));

    var joined = await q.Join(db.Assignments.AsNoTracking(), a => a.TaskAssignmentId, assignment => assignment.Id, (a, assignment) => new { Attempt = a, Assignment = assignment })
        .Where(x => !request.CourseId.HasValue || x.Assignment.CourseId == request.CourseId.Value)
        .Select(x => new
        {
            userId = x.Attempt.UserId,
            assignmentId = x.Attempt.TaskAssignmentId,
            rating = x.Assignment.Rating,
            submittedAt = x.Attempt.SubmittedAt ?? x.Attempt.CreatedAt,
            kind = x.Attempt.Kind
        })
        .ToListAsync(ct);
    return Results.Ok(joined);
});

app.MapPut("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, AssignmentRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    if (!string.IsNullOrWhiteSpace(request.Title)) assignment.Title = request.Title.Trim();
    assignment.Description = request.Description ?? assignment.Description;
    if (!string.IsNullOrWhiteSpace(request.Type)) assignment.Type = request.Type.Trim();
    if (!string.IsNullOrWhiteSpace(request.Language)) assignment.Language = NormalizeLanguage(request.Language) ?? assignment.Language;
    if (request.AllowedLanguages != null) assignment.AllowedLanguagesCsv = NormalizeLanguagesCsv(request.AllowedLanguages);
    if (request.Tags != null) assignment.Tags = request.Tags;
    if (request.Difficulty.HasValue) assignment.Difficulty = Math.Clamp(request.Difficulty.Value, 1, 3);
    if (request.Rating.HasValue) assignment.Rating = Math.Max(0, request.Rating.Value);
    if (request.StarterCode != null) assignment.StarterCode = request.StarterCode;
    var nextType = !string.IsNullOrWhiteSpace(request.Type) ? request.Type.Trim() : assignment.Type;
    if (string.Equals(nextType, "image-test", StringComparison.OrdinalIgnoreCase))
    {
        // Image-test keeps its reference/spec in TestsJson. Do not overwrite it with []
        // from the generic editor payload when only metadata/rules are saved.
        var hasRealSpec = HasMeaningfulJsonText(request.TestsJson) || HasMeaningfulJsonElement(request.Tests) || HasMeaningfulJsonElement(request.TestCases);
        if (hasRealSpec || request.ImageTestReferenceKey != null || request.ImageTestSimilarityThreshold.HasValue)
        {
            assignment.TestsJson = (await MergeAndMaterializeImageTestPayloadAsync(hasRealSpec ? request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases) : assignment.TestsJson, request, assignment.Id, clients, cfg, ct)).ToJsonString(JsonOptions());
        }
    }
    else if (request.TestsJson != null || request.Tests.HasValue || request.TestCases.HasValue) assignment.TestsJson = request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases);
    if (request.CodeForbiddenCalls != null) assignment.CodeForbiddenCallsJson = StringArrayJson(request.CodeForbiddenCalls);
    if (request.CodeRequiredCalls != null) assignment.CodeRequiredCallsJson = StringArrayJson(request.CodeRequiredCalls);
    if (request.IsVisible.HasValue) assignment.IsVisible = request.IsVisible.Value;
    if (request.IsHidden.HasValue) assignment.IsVisible = !request.IsHidden.Value;
    assignment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment, includeSensitive: true));
});

app.MapDelete("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    db.Attempts.RemoveRange(await db.Attempts.Where(x => x.TaskAssignmentId == assignmentId).ToListAsync());
    db.Assignments.Remove(assignment);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Задание удалено.", deleted = assignmentId });
});

app.MapPatch("/api/assignments/{assignmentId:guid}/sort", async (Guid assignmentId, SortRequest request, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    assignment.Sort = request.Sort;
    assignment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment, includeSensitive: true));
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
    return Results.Ok(ToDto(assignment, includeSensitive: true));
});

app.MapPatch("/api/assignments/{assignmentId:guid}/visibility", async (Guid assignmentId, VisibilityRequest request, TasksDbContext db) =>
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound();
    assignment.IsVisible = request.IsVisible;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(assignment, includeSensitive: true));
});

app.MapGet("/api/task-tests/{assignmentId:guid}/edit", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db) =>
{
    if (!IsEditor(http, cfg)) return Results.Json(new { message = "Для редактирования теста нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    return Results.Ok(TaskSpecToJsonObject(ReadTaskSpec(assignment)));
});
app.MapPut("/api/task-tests/{assignmentId:guid}/edit", async (Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db) => IsEditor(http, cfg) ? await SaveSpec(assignmentId, payload, db, kind: "test") : Results.Json(new { message = "Для редактирования теста нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden));
app.MapPost("/api/task-tests/{assignmentId:guid}/start", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) => await StartTest(assignmentId, http, cfg, db, clients, ct));
app.MapPost("/api/task-tests/{assignmentId:guid}/submit", async (Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (CheckUserRateLimit(http, "task-submit") is { } limited) return limited;
    return await SubmitTest(assignmentId, payload, http, cfg, db, clients, ct);
});

app.MapGet("/api/math-tasks/{assignmentId:guid}/edit", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db) =>
{
    if (!IsEditor(http, cfg)) return Results.Json(new { message = "Для редактирования math-задания нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    return Results.Ok(MathSpecToJsonObject(ReadMathSpec(assignment)));
});
app.MapPut("/api/math-tasks/{assignmentId:guid}/edit", async (Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db) => IsEditor(http, cfg) ? await SaveSpec(assignmentId, payload, db, kind: "math") : Results.Json(new { message = "Для редактирования math-задания нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden));
app.MapPost("/api/math-tasks/{assignmentId:guid}/start", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) => await StartMath(assignmentId, http, cfg, db, clients, ct));
app.MapPost("/api/math-tasks/{assignmentId:guid}/submit", async (Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (CheckUserRateLimit(http, "task-submit") is { } limited) return limited;
    return await SubmitMath(assignmentId, payload, http, cfg, db, clients, ct);
});

app.MapGet("/api/me/test-attempts", async (HttpContext http, IConfiguration cfg, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
    Results.Ok(await ListAttempts("test", TaskForgeRequestSecurity.UserId(http, cfg), courseId, assignmentId, days, skip, take, db)));
app.MapGet("/api/me/test-attempts/{attemptId:guid}", async (Guid attemptId, HttpContext http, IConfiguration cfg, TasksDbContext db) => await ReviewAttempt(attemptId, "test", TaskForgeRequestSecurity.UserId(http, cfg), false, db));
app.MapGet("/api/me/math-attempts", async (HttpContext http, IConfiguration cfg, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
    Results.Ok(await ListAttempts("math", TaskForgeRequestSecurity.UserId(http, cfg), courseId, assignmentId, days, skip, take, db)));
app.MapGet("/api/me/math-attempts/{attemptId:guid}", async (Guid attemptId, HttpContext http, IConfiguration cfg, TasksDbContext db) => await ReviewAttempt(attemptId, "math", TaskForgeRequestSecurity.UserId(http, cfg), false, db));
app.MapGet("/api/admin/users/{userId:guid}/test-attempts", async (Guid userId, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50) => Results.Ok(await ListAttempts("test", userId, courseId, assignmentId, days, skip, take, db)));
app.MapGet("/api/admin/users/{userId:guid}/math-attempts", async (Guid userId, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50) => Results.Ok(await ListAttempts("math", userId, courseId, assignmentId, days, skip, take, db)));
app.MapGet("/api/admin/test-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db) => await ReviewAttempt(attemptId, "test", null, true, db));
app.MapGet("/api/admin/math-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db) => await ReviewAttempt(attemptId, "math", null, true, db));
app.MapDelete("/api/admin/test-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db) => await DeleteAttempt(attemptId, "test", db));
app.MapDelete("/api/admin/math-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db) => await DeleteAttempt(attemptId, "math", db));
app.MapGet("/api/admin/assignments/{assignmentId:guid}/insights", async (Guid assignmentId, TasksDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

    var rows = await db.Attempts.AsNoTracking().Where(x => x.TaskAssignmentId == assignmentId && x.SubmittedAt != null).OrderByDescending(x => x.SubmittedAt).ToListAsync(ct);
    var users = await LoadUserSummariesAsync(rows.Select(x => x.UserId).Distinct(), cfg, httpFactory, ct);
    var testRows = rows.Where(x => string.Equals(x.Kind, "test", StringComparison.OrdinalIgnoreCase)).ToList();
    var mathRows = rows.Where(x => string.Equals(x.Kind, "math", StringComparison.OrdinalIgnoreCase)).ToList();
    var uniqueUsers = rows.Select(x => x.UserId).Distinct().Count();
    var successUsers = rows.Where(x => x.Passed).Select(x => x.UserId).Distinct().Count();
    var avgScore = rows.Count == 0 ? 0 : Math.Round(rows.Average(x => x.ScorePercent), 1);
    var courseTitle = await LoadCourseTitleAsync(assignment.CourseId, cfg, httpFactory, ct);

    var solvers = rows.GroupBy(x => x.UserId).Select(g =>
    {
        var user = users.GetValueOrDefault(g.Key);
        return new
        {
            userId = g.Key,
            fullName = UserLabel(user),
            displayName = UserLabel(user),
            email = user?.Email ?? user?.MaskedEmail,
            attempts = g.Count(),
            passed = g.Count(x => x.Passed),
            successRate = Percent(g.Count(x => x.Passed), g.Count()),
            bestScore = g.Max(x => x.ScorePercent),
            lastActivityAtUtc = g.Max(x => x.SubmittedAt ?? x.CreatedAt)
        };
    }).OrderByDescending(x => x.passed).ThenByDescending(x => x.bestScore).ThenByDescending(x => x.lastActivityAtUtc).Take(50).ToList();

    var recent = rows.Take(100).Select(x =>
    {
        var user = users.GetValueOrDefault(x.UserId);
        var created = x.SubmittedAt ?? x.CreatedAt;
        return new
        {
            attemptId = x.Id,
            userId = x.UserId,
            fullName = UserLabel(user),
            displayName = UserLabel(user),
            email = user?.Email ?? user?.MaskedEmail,
            sourceKind = x.Kind,
            kind = x.Kind,
            status = x.Passed ? "passed" : "failed",
            passed = x.Passed,
            scorePercent = x.ScorePercent,
            durationSeconds = Math.Max(0, (int)Math.Round(((x.SubmittedAt ?? x.UpdatedAt) - x.StartedAt).TotalSeconds)),
            createdAtUtc = created,
            submittedAtUtc = x.SubmittedAt
        };
    }).ToList();

    return Results.Ok(new
    {
        assignmentId,
        title = assignment.Title,
        assignmentTitle = assignment.Title,
        courseId = assignment.CourseId,
        courseTitle = courseTitle,
        type = assignment.Type,
        language = assignment.Language,
        rating = assignment.Rating,
        difficulty = assignment.Difficulty,
        attempts = rows.Count,
        solved = rows.Count(x => x.Passed),
        averageScore = avgScore,
        uniqueUsers,
        successUsers,
        codeAttempts = 0,
        passedCodeAttempts = 0,
        testAttempts = testRows.Count,
        passedTests = testRows.Count(x => x.Passed),
        imageAttempts = 0,
        passedImages = 0,
        mathAttempts = mathRows.Count,
        passedMath = mathRows.Count(x => x.Passed),
        avgReviewSeconds = rows.Count == 0 ? 0 : Math.Round(rows.Average(x => Math.Max(0, ((x.SubmittedAt ?? x.UpdatedAt) - x.StartedAt).TotalSeconds)), 1),
        avgTestScore = avgScore,
        languages = string.IsNullOrWhiteSpace(assignment.Language) ? Array.Empty<object>() : new object[] { new { label = assignment.Language, value = rows.Count } },
        solvers,
        recentActivity = recent
    });
});
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/reference", async (Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (CheckUserRateLimit(http, "image-test") is { } limited) return limited;
    if (!IsEditor(http, cfg)) return Results.Json(new { message = "Для загрузки эталона нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var form = await req.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file == null || file.Length == 0) return Problem(400, "IMAGE_REFERENCE_REQUIRED", "request.validation", "Выберите эталонную картинку для image-test.");
    if (file.Length > MaxImageUploadBytes(cfg)) return Problem(413, "IMAGE_REFERENCE_TOO_LARGE", "request.validation", $"Эталонная картинка слишком большая. Максимум: {MaxImageUploadBytes(cfg) / 1024 / 1024} МБ.");
    var threshold = form.TryGetValue("threshold", out var t) && int.TryParse(t, out var tv) ? Math.Clamp(tv, 0, 100) : 90;
    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var uploaded = await UploadImageBytesToFilesApiAsync(clients, cfg, ms.ToArray(), file.FileName, string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, $"image-tests/reference/{assignmentId:N}", ct);
    var payload = JsonNode.Parse(assignment.TestsJson ?? "{}") as JsonObject ?? new JsonObject();
    payload["imageTestReferenceKey"] = uploaded.Key;
    payload["imageTestReferenceUrl"] = uploaded.PrivateUrl;
    payload["imageTestSimilarityThreshold"] = threshold;
    payload["referenceFileName"] = uploaded.FileName;
    payload["referenceContentType"] = uploaded.ContentType;
    payload["referenceSize"] = uploaded.Size;
    RemoveInlineImageFields(payload);
    assignment.Type = "image-test";
    assignment.TestsJson = payload.ToJsonString(JsonOptions());
    assignment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { assignmentId, key = uploaded.Key, url = uploaded.PrivateUrl, privateUrl = uploaded.PrivateUrl, threshold, storage = "minio" });
}).DisableAntiforgery();
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/compare", async (Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients) =>
{
    if (CheckUserRateLimit(http, "image-test") is { } limited) return limited;
    return await CompareImageUpload(assignmentId, req, http, cfg, db, clients);
});
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/run-code", async (Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg) =>
{
    if (CheckUserRateLimit(http, "image-test") is { } limited) return limited;
    return await RenderImageCode(assignmentId, request, http, db, clients, cfg);
});
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/compare-code", async (Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg) =>
{
    if (CheckUserRateLimit(http, "image-test") is { } limited) return limited;
    return await CompareImageCode(assignmentId, request, db, clients, cfg, submit: false, context: http);
});
app.MapPost("/api/assignments/{assignmentId:guid}/image-test/submit-code", async (Guid assignmentId, ImageCodeRequest request, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients) =>
{
    if (CheckUserRateLimit(http, "image-test") is { } limited) return limited;
    return await CompareImageCode(assignmentId, request, db, clients, cfg, submit: true, context: http);
});

app.Run();

static IResult? CheckUserRateLimit(HttpContext http, string bucket)
{
    var userId = http.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User?.FindFirstValue("sub") ?? "anonymous";
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var key = $"{bucket}:{userId}:{ip}";
    if (TaskForgeApiRateLimiters.Allow(bucket, key)) return null;
    return Results.Json(new { message = "Слишком много запросов. Подождите немного и попробуйте снова.", code = "RATE_LIMITED" }, statusCode: StatusCodes.Status429TooManyRequests);
}




static async Task<IResult> RenderImageCode(Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg)
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, CancellationToken.None)) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var lang = NormalizeImageLanguage(request.Language ?? assignment.Language);
    var runner = ImageRunnerService(lang);
    if (runner == null) return Problem(400, "IMAGE_LANGUAGE_UNSUPPORTED", "image-test.run-code", "Image-runner доступен для C++/GLUT, C++ Turtle, Pascal GraphABC, Python Turtle и Python matplotlib/Pillow.", lang);
    var policyProblem = await AnalyzeCodePolicyForAssignment(assignment, lang, request.Code ?? string.Empty, clients, cfg, "image-test.run-code");
    if (policyProblem != null) return policyProblem;
    try
    {
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        var response = await client.PostAsJsonAsync($"http://{runner}:8000/render/debug", new { source = request.Code ?? string.Empty, stdin = request.Input, timeoutSeconds = request.TimeoutSeconds ?? 20, debug = true });
        var raw = await response.Content.ReadAsStringAsync();
        return Results.Content(raw, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        return Problem(503, "IMAGE_RUNNER_FAILED", "image-test.run-code", "Не удалось запустить image-runner.", ex.Message);
    }
}

static async Task<IResult> CompareImageCode(Guid assignmentId, ImageCodeRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, bool submit, HttpContext? context = null)
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    if (assignment == null || (context is not null && !await CanUserAccessAssignmentAsync(assignment, context, cfg, clients, CancellationToken.None))) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var currentUserId = submit && context is not null ? RequireUser(context, cfg) : null;
    if (submit && currentUserId == null) return Unauthorized();

    var root = JsonNode.Parse(assignment.TestsJson ?? "{}") as JsonObject ?? new JsonObject();
    var lang = NormalizeImageLanguage(request.Language ?? assignment.Language);
    var runner = ImageRunnerService(lang);
    if (runner == null) return Problem(400, "IMAGE_LANGUAGE_UNSUPPORTED", submit ? "image-test.submit-code" : "image-test.compare-code", "Image-runner доступен для C++/GLUT, C++ Turtle, Pascal GraphABC, Python Turtle и Python matplotlib/Pillow.", lang);

    var cases = ReadImageTestCases(root, request.Input);
    if (cases.Count == 0) return Problem(400, "IMAGE_REFERENCE_MISSING", "image-test.reference", "Для задания не настроены image-тесты: добавьте Input, Expected output и Expected image хотя бы для одного теста.");

    var policyProblem = await AnalyzeCodePolicyForAssignment(assignment, lang, request.Code ?? string.Empty, clients, cfg, submit ? "image-test.submit-code" : "image-test.compare-code");
    if (policyProblem != null) return policyProblem;

    try
    {
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp((request.TimeoutSeconds ?? 20) * Math.Max(1, cases.Count) + 60, 90, 300));
        var runnerTimeout = request.TimeoutSeconds ?? 20;
        var results = new List<ImageCaseResult>();
        var canViewReferenceImages = context != null && IsEditor(context, cfg);

        for (var i = 0; i < cases.Count; i++)
        {
            var test = cases[i];
            var render = await client.PostAsJsonAsync($"http://{runner}:8000/render/debug", new { source = request.Code ?? string.Empty, stdin = test.Input, timeoutSeconds = runnerTimeout, debug = true });
            var renderRaw = await render.Content.ReadAsStringAsync();
            if (!render.IsSuccessStatusCode)
            {
                results.Add(new ImageCaseResult(
                    Index: i + 1,
                    Name: test.Name,
                    Input: test.Input,
                    ExpectedOutput: test.IsHidden ? null : test.ExpectedOutput,
                    ActualOutput: null,
                    StdoutPassed: string.IsNullOrWhiteSpace(test.ExpectedOutput) ? true : false,
                    ImagePassed: false,
                    Passed: false,
                    Similarity: 0,
                    SimilarityPercent: 0,
                    Threshold: test.Threshold,
                    ThresholdPercent: test.Threshold,
                    IsHidden: test.IsHidden,
                    ReferenceUrl: canViewReferenceImages ? test.ExpectedImageUrl : null,
                    SubmittedUrl: null,
                    Stderr: renderRaw,
                    Analyzer: null));
                continue;
            }

            using var renderDoc = JsonDocument.Parse(renderRaw);
            var pngBase64 = renderDoc.RootElement.TryGetProperty("pngBase64", out var p) ? p.GetString() : null;
            var stdout = renderDoc.RootElement.TryGetProperty("stdout", out var so) ? so.GetString() ?? string.Empty : string.Empty;
            var stderr = renderDoc.RootElement.TryGetProperty("stderr", out var se) ? se.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(pngBase64))
            {
                results.Add(new ImageCaseResult(i + 1, test.Name, test.Input, test.IsHidden ? null : test.ExpectedOutput, stdout, StdoutMatches(stdout, test.ExpectedOutput), false, false, 0, 0, test.Threshold, test.Threshold, test.IsHidden, canViewReferenceImages ? test.ExpectedImageUrl : null, null, stderr + "\nRunner завершился без PNG-изображения.", null));
                continue;
            }

            var expected = await LoadExpectedImageAsync(test, clients, cfg, CancellationToken.None);
            var expectedBytes = expected.Bytes;
            var actualBytes = Convert.FromBase64String(pngBase64);
            var actualUrl = $"data:image/png;base64,{pngBase64}";
            if (submit && currentUserId.HasValue)
            {
                var uploadedActual = await UploadImageBytesToFilesApiAsync(clients, cfg, actualBytes, $"case-{i + 1}-actual.png", "image/png", $"image-tests/submissions/{assignmentId:N}/{currentUserId.Value:N}", CancellationToken.None);
                actualUrl = uploadedActual.PrivateUrl ?? PrivateFileUrl(uploadedActual.Key);
            }
            var threshold = Math.Clamp(test.Threshold / 100.0, 0.0, 1.0);
            using var mp = new MultipartFormDataContent();
            var expectedPart = new ByteArrayContent(expectedBytes);
            if (!string.IsNullOrWhiteSpace(expected.ContentType)) expectedPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(expected.ContentType);
            mp.Add(expectedPart, "expected", test.ExpectedImageFileName ?? expected.FileName ?? "expected.png");
            var actualPart = new ByteArrayContent(actualBytes);
            actualPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            mp.Add(actualPart, "actual", "actual.png");
            var compared = await client.PostAsync($"http://image-analyzer:8080/compare?threshold={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}", mp);
            var compareRaw = await compared.Content.ReadAsStringAsync();
            if (!compared.IsSuccessStatusCode)
            {
                results.Add(new ImageCaseResult(i + 1, test.Name, test.Input, test.IsHidden ? null : test.ExpectedOutput, stdout, StdoutMatches(stdout, test.ExpectedOutput), false, false, 0, 0, test.Threshold, test.Threshold, test.IsHidden, canViewReferenceImages ? test.ExpectedImageUrl : null, actualUrl, stderr + "\n" + compareRaw, null));
                continue;
            }
            var analyzer = JsonSerializer.Deserialize<JsonElement>(compareRaw);
            var combined = analyzer.TryGetProperty("combined_similarity", out var c) && c.TryGetDouble(out var cv) ? cv : 0.0;
            var imagePassed = analyzer.TryGetProperty("passed", out var pass) && pass.ValueKind == JsonValueKind.True;
            var similarityPercent = Math.Round(combined * 100, 2);
            var stdoutPassed = StdoutMatches(stdout, test.ExpectedOutput);
            results.Add(new ImageCaseResult(
                Index: i + 1,
                Name: test.Name,
                Input: test.Input,
                ExpectedOutput: test.IsHidden ? null : test.ExpectedOutput,
                ActualOutput: stdout,
                StdoutPassed: stdoutPassed,
                ImagePassed: imagePassed,
                Passed: stdoutPassed && imagePassed,
                Similarity: similarityPercent,
                SimilarityPercent: similarityPercent,
                Threshold: test.Threshold,
                ThresholdPercent: test.Threshold,
                IsHidden: test.IsHidden,
                ReferenceUrl: canViewReferenceImages ? test.ExpectedImageUrl : null,
                SubmittedUrl: actualUrl,
                Stderr: stderr,
                Analyzer: analyzer));
        }

        var passed = results.Count > 0 && results.All(x => x.Passed);
        var passedCount = results.Count(x => x.Passed);
        var similarityPercentInt = results.Count == 0 ? 0 : (int)Math.Round(results.Average(x => x.SimilarityPercent));
        var referenceUrl = canViewReferenceImages ? results.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ReferenceUrl))?.ReferenceUrl ?? ReferenceUrl(root) : null;
        var submittedUrl = results.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SubmittedUrl))?.SubmittedUrl;
        var stdoutJoined = string.Join("\n---\n", results.Select(x => x.ActualOutput).Where(x => !string.IsNullOrWhiteSpace(x)));
        var stderrJoined = string.Join("\n---\n", results.Select(x => x.Stderr).Where(x => !string.IsNullOrWhiteSpace(x)));
        var savedSolutionId = (Guid?)null;

        var resultPayload = new
        {
            assignmentId,
            submitted = submit,
            passed,
            passedCount,
            total = results.Count,
            similarity = similarityPercentInt,
            similarityPercent = similarityPercentInt,
            threshold = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
            thresholdPercent = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
            stdout = stdoutJoined,
            stderr = stderrJoined,
            referenceUrl,
            submittedUrl,
            cases = results
        };

        if (submit && currentUserId.HasValue)
        {
            savedSolutionId = await SaveImageSolutionAsync(
                assignmentId,
                currentUserId.Value,
                lang,
                request.Code ?? string.Empty,
                similarityPercentInt,
                passed,
                JsonSerializer.SerializeToElement(resultPayload, JsonOptions()),
                cfg,
                clients);
        }

        return Results.Ok(new
        {
            id = savedSolutionId,
            solutionId = savedSolutionId,
            assignmentId,
            submitted = submit,
            passed,
            passedCount,
            total = results.Count,
            similarity = similarityPercentInt,
            similarityPercent = similarityPercentInt,
            threshold = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
            thresholdPercent = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
            stdout = stdoutJoined,
            stderr = stderrJoined,
            referenceUrl,
            submittedUrl,
            cases = results
        });
    }
    catch (Exception ex)
    {
        return Problem(503, "IMAGE_CODE_COMPARE_FAILED", submit ? "image-test.submit-code" : "image-test.compare-code", "Не удалось выполнить image-code pipeline.", ex.Message);
    }
}

static List<ImageTestCaseSpec> ReadImageTestCases(JsonObject root, string? fallbackInput)
{
    var list = new List<ImageTestCaseSpec>();
    foreach (var prop in new[] { "testCases", "tests", "cases" })
    {
        if (root[prop] is not JsonArray arr) continue;
        var index = 0;
        foreach (var node in arr)
        {
            index++;
            if (node is not JsonObject o) continue;
            var key = NodeString(o, "expectedImageKey") ?? NodeString(o, "referenceKey") ?? NodeString(o, "imageKey") ?? NodeString(o, "imageTestReferenceKey");
            var image = StripDataUrl(NodeString(o, "expectedImageBase64") ?? NodeString(o, "referenceBase64") ?? NodeString(o, "imageBase64"));
            if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(image)) continue;
            var threshold = NodeInt(o, "threshold", NodeInt(o, "thresholdPercent", NodeInt(o, "imageTestSimilarityThreshold", JsonNodeInt(root, "imageTestSimilarityThreshold", 90))));
            var contentType = NodeString(o, "expectedImageContentType") ?? NodeString(o, "referenceContentType") ?? "image/png";
            var fileName = NodeString(o, "expectedImageFileName") ?? NodeString(o, "referenceFileName") ?? "expected.png";
            var name = NodeString(o, "name") ?? NodeString(o, "title") ?? $"Тест {index}";
            var input = NodeString(o, "input") ?? NodeString(o, "stdin") ?? string.Empty;
            var expected = NodeString(o, "expectedOutput") ?? NodeString(o, "expected") ?? NodeString(o, "stdout") ?? string.Empty;
            var hidden = NodeBool(o, "isHidden") || NodeBool(o, "hidden");
            list.Add(new ImageTestCaseSpec(name, input, expected, image, key, Math.Clamp(threshold, 0, 100), hidden, contentType, fileName));
        }
        if (list.Count > 0) return list;
    }

    var legacyKey = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey") ?? NodeString(root, "imageKey");
    var legacy = StripDataUrl(NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64"));
    if (!string.IsNullOrWhiteSpace(legacyKey) || !string.IsNullOrWhiteSpace(legacy))
    {
        var threshold = JsonNodeInt(root, "imageTestSimilarityThreshold", JsonNodeInt(root, "threshold", 90));
        var contentType = NodeString(root, "referenceContentType") ?? NodeString(root, "expectedImageContentType") ?? "image/png";
        var fileName = NodeString(root, "referenceFileName") ?? NodeString(root, "expectedImageFileName") ?? "expected.png";
        var expected = NodeString(root, "expectedOutput") ?? string.Empty;
        list.Add(new ImageTestCaseSpec("Основной тест", fallbackInput ?? string.Empty, expected, legacy, legacyKey, Math.Clamp(threshold, 0, 100), false, contentType, fileName));
    }

    return list;
}

static string? NodeString(JsonObject o, string name) => o.TryGetPropertyValue(name, out var n) && n is not null ? n.ToString() : null;
static int NodeInt(JsonObject o, string name, int fallback) => int.TryParse(NodeString(o, name), out var v) ? v : fallback;
static bool NodeBool(JsonObject o, string name) => bool.TryParse(NodeString(o, name), out var v) && v;
static int JsonNodeInt(JsonObject o, string name, int fallback) => int.TryParse(NodeString(o, name), out var v) ? v : fallback;
static string? StripDataUrl(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var s = value.Trim();
    if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        var comma = s.IndexOf(',');
        if (comma >= 0) s = s[(comma + 1)..];
    }
    return s;
}

static bool StdoutMatches(string actual, string? expected)
{
    if (string.IsNullOrWhiteSpace(expected)) return true;
    static string Norm(string v) => string.Join("\n", (v ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(x => x.TrimEnd())).Trim();
    return string.Equals(Norm(actual), Norm(expected), StringComparison.OrdinalIgnoreCase);
}


static async Task<IResult?> AnalyzeCodePolicyForAssignment(Assignment assignment, string language, string code, IHttpClientFactory clients, IConfiguration cfg, string stage)
{
    if (!cfg.GetValue("CodeAnalyzer:Enabled", true)) return null;
    var baseUrl = (cfg["CodeAnalyzer:Url"] ?? "http://code-analyzer:8080").TrimEnd('/');
    var forbidden = ParseStringArrayJson(assignment.CodeForbiddenCallsJson);
    var required = ParseStringArrayJson(assignment.CodeRequiredCallsJson);
    var client = clients.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("CodeAnalyzer:TimeoutSeconds", 8), 2, 60));

    try
    {
        using var response = await client.PostAsJsonAsync($"{baseUrl}/analyze", new AnalyzerRequest(NormalizeLanguage(language) ?? language, code, null, forbidden.Length > 0 ? forbidden : null, required.Length > 0 ? required : null), JsonOptions());
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return Problem(503, "CODE_ANALYZER_FAILED", stage, "Сервис анализа кода временно недоступен.");
        }
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        var root = doc.RootElement.Clone();
        var ok = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("ok", out var okProp)
            && okProp.ValueKind is JsonValueKind.True or JsonValueKind.False
            && okProp.GetBoolean();
        if (ok) return null;
        var message = BuildImagePolicyMessage(root);
        return Results.Json(new { status = 400, code = "CODE_POLICY_FAILED", stage, message, severity = "warning" }, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (Exception ex)
    {
        return Problem(503, "CODE_ANALYZER_FAILED", stage, "Сервис анализа кода временно недоступен.", ex.Message);
    }
}

static string BuildImagePolicyMessage(JsonElement root)
{
    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        return "Код содержит запрещённые конструкции.";
    var lines = errors.EnumerateArray()
        .Where(e => e.ValueKind == JsonValueKind.Object)
        .Where(e => !IsSensitiveAnalyzerPattern(StringProp(e, "pattern_id") ?? StringProp(e, "patternId")))
        .Select(e => StringProp(e, "message"))
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(8)
        .ToArray();
    return lines.Length == 0
        ? "Код использует системные возможности, которые нельзя запускать в песочнице."
        : string.Join("; ", lines);
}

static bool IsSensitiveAnalyzerPattern(string? patternId)
{
    var id = (patternId ?? string.Empty).Trim().ToLowerInvariant();
    return id.StartsWith("py.") || id.StartsWith("js.") || id.StartsWith("c.") || id.StartsWith("cpp.") || id.StartsWith("cs.") || id.StartsWith("java.") || id.StartsWith("pas.");
}

static string NormalizeImageLanguage(string? lang) => (lang ?? "cpp").Trim().ToLowerInvariant() switch
{
    "c++" or "cpp" or "g++" or "gcc" or "cxx" or "glut" or "cpp-glut" or "c++-glut" or "turtle" or "cpp-turtle" or "c++-turtle" => "cpp",
    "pas" or "pascal" or "pabc" or "graphabc" or "pascalabc" => "pascal",
    "py" or "python" or "python3" or "python-turtle" or "turtle-py" or "matplotlib" or "pillow" => "python",
    var x => x
};
static string? ImageRunnerService(string lang) => lang switch { "cpp" => "image-cpp-runner", "pascal" => "image-pascal-runner", "python" => "image-python-runner", _ => null };
static string? ReferenceUrl(JsonObject root)
{
    var key = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey") ?? NodeString(root, "imageKey");
    if (!string.IsNullOrWhiteSpace(key)) return PrivateFileUrl(key);

    // Legacy fallback only. New image-test v2 stores expected images in MinIO and keeps only keys in TestsJson.
    var base64 = NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64");
    if (!string.IsNullOrWhiteSpace(base64))
    {
        var contentType = NodeString(root, "referenceContentType") ?? NodeString(root, "expectedImageContentType") ?? "image/png";
        return base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? base64 : $"data:{contentType};base64,{base64}";
    }
    return null;
}

static async Task<Guid?> SaveImageSolutionAsync(Guid assignmentId, Guid userId, string language, string code, int similarityPercent, bool passed, JsonElement result, IConfiguration cfg, IHttpClientFactory clients)
{
    var baseUrl = ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080");
    var client = clients.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(15);
    using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/image-solutions")
    {
        Content = JsonContent.Create(new InternalImageSolutionRequest(userId, assignmentId, language, code, similarityPercent, passed, result), options: JsonOptions())
    };
    AddInternalKey(msg, cfg);

    try
    {
        using var response = await client.SendAsync(msg);
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.ToString(), out var savedId) ? savedId : null;
    }
    catch
    {
        return null;
    }
}

static async Task<LoadedImageBytes> LoadExpectedImageAsync(ImageTestCaseSpec test, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(test.ExpectedImageKey))
    {
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var url = $"{ServiceUrl(cfg, "FilesApi", "http://files-api:8080")}/api/internal/files/{EscapeFileKeyForUrl(test.ExpectedImageKey)}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        AddInternalKey(msg, cfg);
        using var response = await client.SendAsync(msg, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Не удалось получить эталонную картинку из MinIO/files-api: {(int)response.StatusCode} {System.Text.Encoding.UTF8.GetString(bytes)}");
        var contentType = response.Content.Headers.ContentType?.MediaType ?? test.ExpectedImageContentType ?? "image/png";
        return new LoadedImageBytes(bytes, contentType, test.ExpectedImageFileName ?? Path.GetFileName(test.ExpectedImageKey));
    }

    if (!string.IsNullOrWhiteSpace(test.ExpectedImageBase64))
    {
        var decoded = DecodeImageBase64(test.ExpectedImageBase64, test.ExpectedImageContentType ?? "image/png");
        return new LoadedImageBytes(decoded.Bytes, decoded.ContentType, test.ExpectedImageFileName ?? "expected.png");
    }

    throw new InvalidOperationException("У image-test кейса нет expectedImageKey и legacy expectedImageBase64.");
}

static async Task<UploadedFileDto> UploadImageBytesToFilesApiAsync(IHttpClientFactory clients, IConfiguration cfg, byte[] bytes, string? fileName, string contentType, string folder, CancellationToken ct)
{
    if (bytes.Length == 0) throw new InvalidOperationException("Нельзя загрузить пустую эталонную картинку.");
    var safeFileName = string.IsNullOrWhiteSpace(fileName) ? $"image{ExtensionForContentType(contentType)}" : Path.GetFileName(fileName);
    var client = clients.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(60);
    using var mp = new MultipartFormDataContent();
    var part = new ByteArrayContent(bytes);
    part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
    mp.Add(part, "file", safeFileName);
    mp.Add(new StringContent(folder), "folder");
    using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "FilesApi", "http://files-api:8080")}/api/internal/files/images") { Content = mp };
    AddInternalKey(msg, cfg);
    using var response = await client.SendAsync(msg, ct);
    var raw = await response.Content.ReadAsStringAsync(ct);
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"files-api не сохранил expected image в MinIO: {(int)response.StatusCode} {raw}");
    using var doc = JsonDocument.Parse(raw);
    var root = doc.RootElement;
    var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("files-api не вернул key после загрузки expected image.");
    var privateUrl = root.TryGetProperty("privateUrl", out var pu) ? pu.GetString() : PrivateFileUrl(key);
    var returnedContentType = root.TryGetProperty("contentType", out var ctProp) ? ctProp.GetString() : contentType;
    var returnedFileName = root.TryGetProperty("fileName", out var fn) ? fn.GetString() : safeFileName;
    var size = root.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : bytes.Length;
    return new UploadedFileDto(key, privateUrl, returnedContentType ?? contentType, returnedFileName ?? safeFileName, size);
}

static string PrivateFileUrl(string key) => $"/api/private-files/{Uri.EscapeDataString(key)}";
static string EscapeFileKeyForUrl(string key) => string.Join("/", (key ?? string.Empty).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

static string ServiceUrl(IConfiguration cfg, string name, string fallback)
{
    return (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');
}

static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
{
    var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
    if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
}


static async Task<IResult> CompareImageUpload(Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients)
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
    if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, CancellationToken.None)) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var root = JsonNode.Parse(assignment.TestsJson ?? "{}") as JsonObject ?? new JsonObject();
    var referenceKey = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey") ?? NodeString(root, "imageKey");
    var referenceBase64 = StripDataUrl(NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64"));
    if (string.IsNullOrWhiteSpace(referenceKey) && string.IsNullOrWhiteSpace(referenceBase64)) return Problem(400, "IMAGE_REFERENCE_MISSING", "image-test.reference", "Для задания ещё не загружена эталонная картинка.");
    var form = await req.ReadFormAsync();
    var actual = form.Files.FirstOrDefault();
    if (actual == null || actual.Length == 0) return Problem(400, "IMAGE_ACTUAL_REQUIRED", "request.validation", "Выберите изображение для сравнения.");
    if (actual.Length > MaxImageUploadBytes(cfg)) return Problem(413, "IMAGE_ACTUAL_TOO_LARGE", "request.validation", $"Изображение для сравнения слишком большое. Максимум: {MaxImageUploadBytes(cfg) / 1024 / 1024} МБ.");
    var expectedSpec = new ImageTestCaseSpec("Основной тест", string.Empty, string.Empty, referenceBase64, referenceKey, JsonInt(assignment.TestsJson, "imageTestSimilarityThreshold", 90), false, NodeString(root, "referenceContentType") ?? "image/png", NodeString(root, "referenceFileName") ?? "expected.png");
    var expected = await LoadExpectedImageAsync(expectedSpec, clients, cfg, CancellationToken.None);
    await using var actualMs = new MemoryStream();
    await actual.CopyToAsync(actualMs);
    var thresholdPercent = JsonInt(assignment.TestsJson, "imageTestSimilarityThreshold", 90);
    var threshold = Math.Clamp(thresholdPercent / 100.0, 0.0, 1.0);
    try
    {
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var mp = new MultipartFormDataContent();
        var expectedPart = new ByteArrayContent(expected.Bytes);
        expectedPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(expected.ContentType ?? "application/octet-stream");
        mp.Add(expectedPart, "expected", expected.FileName ?? "expected.png");
        var actualPart = new ByteArrayContent(actualMs.ToArray());
        actualPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(actual.ContentType) ? "application/octet-stream" : actual.ContentType);
        mp.Add(actualPart, "actual", actual.FileName);
        var response = await client.PostAsync($"http://image-analyzer:8080/compare?threshold={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}", mp);
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return Problem(503, "IMAGE_ANALYZER_FAILED", "image-test.analyzer", "image-analyzer не смог сравнить изображения. Проверьте контейнер image-analyzer и формат файлов.", raw);
        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        var combined = json.TryGetProperty("combined_similarity", out var c) && c.TryGetDouble(out var cv) ? cv : 0.0;
        var clip = json.TryGetProperty("clip_similarity", out var cl) && cl.TryGetDouble(out var clv) ? clv : combined;
        var passed = json.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True;
        return Results.Ok(new { passed, similarity = Math.Round(combined * 100, 2), clipSimilarity = Math.Round(clip * 100, 2), threshold = thresholdPercent, analyzer = json, referenceUrl = IsEditor(http, cfg) ? expectedSpec.ExpectedImageUrl : null });
    }
    catch (Exception ex)
    {
        return Problem(503, "IMAGE_COMPARE_FAILED", "image-test.analyzer", "Не удалось выполнить сравнение через image-analyzer.", ex.Message);
    }
}

static async Task<IResult> StartTest(Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
{
    var userId = RequireUser(http, cfg);
    if (userId == null) return Unauthorized();
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
    if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, ct)) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var spec = ReadTaskSpec(assignment);
    if (spec.Questions.Count == 0) return Problem(400, "TEST_HAS_NO_QUESTIONS", "tasks.test.start", "В тесте пока нет вопросов.");
    var active = await db.Attempts.FirstOrDefaultAsync(x => x.Kind == "test" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value && x.SubmittedAt == null);
    if (active != null) return Results.Ok(TestStartDto(active, spec));
    var used = await db.Attempts.CountAsync(x => x.Kind == "test" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
    var max = spec.Settings.MaxAttempts <= 0 ? int.MaxValue : spec.Settings.MaxAttempts;
    if (used + 1 > max) return Results.Json(new { message = "Достигнут лимит попыток.", code = "ATTEMPT_LIMIT_REACHED" }, statusCode: StatusCodes.Status409Conflict);
    var attempt = new TaskAttempt { Kind = "test", TaskAssignmentId = assignmentId, UserId = userId.Value, AttemptNumber = used + 1, TimeLimitSeconds = TimeLimitFor(spec.Settings.AttemptTimeLimitsSeconds, used + 1) };
    attempt.OrderJson = JsonSerializer.Serialize(OrderedIds(spec.Questions.Select(x => x.Id), spec.Settings.ShuffleQuestions, attempt.Id), JsonOptions());
    db.Attempts.Add(attempt);
    await db.SaveChangesAsync();
    return Results.Ok(TestStartDto(attempt, spec));
}

static async Task<IResult> SubmitTest(Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
{
    var userId = RequireUser(http, cfg);
    if (userId == null) return Unauthorized();
    var attemptId = GuidProp(payload, "attemptId");
    if (attemptId == Guid.Empty) return Problem(400, "ATTEMPT_ID_REQUIRED", "tasks.test.submit", "Не передан attemptId.");
    var attempt = await db.Attempts.FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == "test" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
    if (attempt == null) return Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
    if (attempt.SubmittedAt != null) return Problem(400, "ATTEMPT_ALREADY_SUBMITTED", "tasks.test.submit", "Эта попытка уже была отправлена.");
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
    if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, ct)) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var spec = ReadTaskSpec(assignment);
    var answers = AnswersArray(payload, "answers");
    var byAnswer = answers.GroupBy(x => x.QuestionId).ToDictionary(x => x.Key, x => x.First());
    var order = ParseGuidList(attempt.OrderJson);
    if (order.Count == 0) order = spec.Questions.Select(x => x.Id).ToList();
    var map = spec.Questions.ToDictionary(x => x.Id);
    var total = 0; var correct = 0;
    var review = new JsonArray();
    foreach (var id in order)
    {
        if (!map.TryGetValue(id, out var q)) continue;
        total++;
        byAnswer.TryGetValue(id, out var ans);
        var ok = IsTestCorrect(q, ans);
        if (ok) correct++;
        review.Add(TestQuestionReviewNode(q, ans, ok));
    }
    var score = total == 0 ? 0 : (int)Math.Floor(correct * 100.0 / total);
    attempt.SubmittedAt = DateTimeOffset.UtcNow;
    attempt.TimeExpired = IsTimeExpired(attempt);
    attempt.TotalUnits = total; attempt.CorrectUnits = correct; attempt.TotalScore = total; attempt.EarnedScore = correct;
    attempt.ScorePercent = attempt.TimeExpired ? 0 : score;
    attempt.Passed = !attempt.TimeExpired && attempt.ScorePercent >= spec.Settings.PassPercent;
    attempt.AnswersJson = JsonSerializer.Serialize(answers, JsonOptions());
    attempt.ReviewJson = new JsonObject { ["questions"] = review }.ToJsonString(JsonOptions());
    attempt.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = spec.Settings.MaxAttempts, passPercent = spec.Settings.PassPercent, totalQuestions = total, correctQuestions = correct, scorePercent = attempt.ScorePercent, attempt.TimeExpired, attempt.Passed });
}

static async Task<IResult> StartMath(Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
{
    var userId = RequireUser(http, cfg);
    if (userId == null) return Unauthorized();
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
    if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, ct)) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var spec = ReadMathSpec(assignment);
    if (spec.Blocks.Count == 0) return Problem(400, "MATH_HAS_NO_BLOCKS", "tasks.math.start", "В math-задании пока нет блоков.");
    var active = await db.Attempts.FirstOrDefaultAsync(x => x.Kind == "math" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value && x.SubmittedAt == null);
    if (active != null) return Results.Ok(MathStartDto(active, spec));
    var used = await db.Attempts.CountAsync(x => x.Kind == "math" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
    var max = spec.Settings.MaxAttempts <= 0 ? int.MaxValue : spec.Settings.MaxAttempts;
    if (used + 1 > max) return Results.Json(new { message = "Достигнут лимит попыток.", code = "ATTEMPT_LIMIT_REACHED" }, statusCode: StatusCodes.Status409Conflict);
    var attempt = new TaskAttempt { Kind = "math", TaskAssignmentId = assignmentId, UserId = userId.Value, AttemptNumber = used + 1, TimeLimitSeconds = TimeLimitFor(spec.Settings.AttemptTimeLimitsSeconds, used + 1) };
    attempt.OrderJson = JsonSerializer.Serialize(OrderedIds(spec.Blocks.Select(x => x.Id), spec.Settings.ShuffleBlocks, attempt.Id), JsonOptions());
    db.Attempts.Add(attempt);
    await db.SaveChangesAsync();
    return Results.Ok(MathStartDto(attempt, spec));
}

static async Task<IResult> SubmitMath(Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
{
    var userId = RequireUser(http, cfg);
    if (userId == null) return Unauthorized();
    var attemptId = GuidProp(payload, "attemptId");
    if (attemptId == Guid.Empty) return Problem(400, "ATTEMPT_ID_REQUIRED", "tasks.math.submit", "Не передан attemptId.");
    var attempt = await db.Attempts.FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == "math" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
    if (attempt == null) return Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
    if (attempt.SubmittedAt != null) return Problem(400, "ATTEMPT_ALREADY_SUBMITTED", "tasks.math.submit", "Эта попытка уже была отправлена.");
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
    if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, ct)) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var spec = ReadMathSpec(assignment);
    var answers = MathAnswersArray(payload, "answers");
    var byAnswer = answers.GroupBy(x => x.BlockId).ToDictionary(x => x.Key, x => x.First());
    var order = ParseGuidList(attempt.OrderJson);
    if (order.Count == 0) order = spec.Blocks.Select(x => x.Id).ToList();
    var map = spec.Blocks.ToDictionary(x => x.Id);
    var totalScore = 0; var earned = 0; var correct = 0;
    var review = new JsonArray();
    foreach (var id in order)
    {
        if (!map.TryGetValue(id, out var b)) continue;
        if (b.Kind == "info") continue;
        totalScore += Math.Max(1, b.Score);
        byAnswer.TryGetValue(id, out var ans);
        var ok = IsMathCorrect(b, ans);
        if (ok) { correct++; earned += Math.Max(1, b.Score); }
        review.Add(MathBlockReviewNode(b, ans, ok));
    }
    var score = totalScore == 0 ? 0 : (int)Math.Floor(earned * 100.0 / totalScore);
    attempt.SubmittedAt = DateTimeOffset.UtcNow;
    attempt.TimeExpired = IsTimeExpired(attempt);
    attempt.TotalUnits = spec.Blocks.Count(x => x.Kind != "info"); attempt.CorrectUnits = correct; attempt.TotalScore = totalScore; attempt.EarnedScore = earned;
    attempt.ScorePercent = attempt.TimeExpired ? 0 : score;
    attempt.Passed = !attempt.TimeExpired && attempt.ScorePercent >= spec.Settings.PassPercent;
    attempt.AnswersJson = JsonSerializer.Serialize(answers, JsonOptions());
    attempt.ReviewJson = new JsonObject { ["blocks"] = review }.ToJsonString(JsonOptions());
    attempt.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = spec.Settings.MaxAttempts, passPercent = spec.Settings.PassPercent, totalScore, earnedScore = earned, scorePercent = attempt.ScorePercent, attempt.TimeExpired, attempt.Passed });
}

static async Task<List<object>> ListAttempts(string kind, Guid? userId, Guid? courseId, Guid? assignmentId, int? days, int skip, int take, TasksDbContext db)
{
    var q = db.Attempts.AsNoTracking().Where(x => x.Kind == kind && x.SubmittedAt != null);
    if (userId.HasValue) q = q.Where(x => x.UserId == userId.Value);
    if (assignmentId.HasValue) q = q.Where(x => x.TaskAssignmentId == assignmentId.Value);
    if (days.HasValue && days.Value > 0) q = q.Where(x => x.SubmittedAt >= DateTimeOffset.UtcNow.AddDays(-days.Value));
    var rows = await q.OrderByDescending(x => x.SubmittedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take <= 0 ? 50 : take, 1, 200)).ToListAsync();
    var assignmentIds = rows.Select(x => x.TaskAssignmentId).Distinct().ToList();
    var map = await db.Assignments.AsNoTracking().Where(x => assignmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
    if (courseId.HasValue) rows = rows.Where(x => map.TryGetValue(x.TaskAssignmentId, out var a) && a.CourseId == courseId.Value).ToList();
    return rows.Select(x =>
    {
        map.TryGetValue(x.TaskAssignmentId, out var a);
        return (object)new { attemptId = x.Id, taskAssignmentId = x.TaskAssignmentId, courseId = a?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = a?.Title ?? "Задание", x.AttemptNumber, submittedAt = x.SubmittedAt, x.ScorePercent, x.Passed, x.TimeExpired, allowReview = true };
    }).ToList();
}

static async Task<IResult> ReviewAttempt(Guid attemptId, string kind, Guid? userId, bool isAdmin, TasksDbContext db)
{
    var attempt = await db.Attempts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == kind);
    if (attempt == null) return Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
    if (!isAdmin && userId.HasValue && attempt.UserId != userId.Value) return Results.Json(new { message = "Нет доступа к этой попытке.", code = "ATTEMPT_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attempt.TaskAssignmentId);
    var review = ParseJson(attempt.ReviewJson);

    if (kind == "math")
    {
        var spec = assignment == null ? null : ReadMathSpec(assignment);
        var allowReview = isAdmin || spec?.Settings.AllowReview == true;
        return Results.Ok(new { attemptId = attempt.Id, attempt.TaskAssignmentId, courseId = assignment?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = assignment?.Title ?? "Задание", attempt.UserId, attempt.AttemptNumber, attempt.StartedAt, submittedAt = attempt.SubmittedAt, passPercent = spec?.Settings.PassPercent ?? 60, attempt.TotalScore, attempt.EarnedScore, attempt.ScorePercent, attempt.Passed, attempt.TimeExpired, allowReview, blocks = allowReview ? JsonPropArray(review, "blocks") : Array.Empty<object>() });
    }

    var testSpec = assignment == null ? null : ReadTaskSpec(assignment);
    var testAllowReview = isAdmin || testSpec?.Settings.AllowReview == true;
    return Results.Ok(new { attemptId = attempt.Id, attempt.TaskAssignmentId, courseId = assignment?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = assignment?.Title ?? "Задание", attempt.UserId, attempt.AttemptNumber, attempt.StartedAt, submittedAt = attempt.SubmittedAt, passPercent = testSpec?.Settings.PassPercent ?? 60, totalQuestions = attempt.TotalUnits, correctQuestions = attempt.CorrectUnits, attempt.ScorePercent, attempt.Passed, attempt.TimeExpired, allowReview = testAllowReview, questions = testAllowReview ? JsonPropArray(review, "questions") : Array.Empty<object>() });
}

static async Task<IResult> DeleteAttempt(Guid attemptId, string kind, TasksDbContext db)
{
    var attempt = await db.Attempts.FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == kind);
    if (attempt == null) return Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
    db.Attempts.Remove(attempt);
    await db.SaveChangesAsync();
    return Results.NoContent();
}

static object TestStartDto(TaskAttempt attempt, TaskSpec spec)
{
    var order = ParseGuidList(attempt.OrderJson);
    var map = spec.Questions.ToDictionary(x => x.Id);
    var questions = (order.Count == 0 ? spec.Questions : order.Where(map.ContainsKey).Select(id => map[id])).Select(x => TestQuestionPublicDto(x, spec.Settings.ShuffleAnswers, attempt.Id)).ToList();
    return new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = spec.Settings.MaxAttempts, passPercent = spec.Settings.PassPercent, attempt.TimeLimitSeconds, startedAt = attempt.StartedAt, startedAtUtc = attempt.StartedAt, spec.Settings.ShuffleQuestions, spec.Settings.ShuffleAnswers, questions };
}
static object MathStartDto(TaskAttempt attempt, MathSpec spec)
{
    var order = ParseGuidList(attempt.OrderJson);
    var map = spec.Blocks.ToDictionary(x => x.Id);
    var blocks = (order.Count == 0 ? spec.Blocks : order.Where(map.ContainsKey).Select(id => map[id])).Select(x => MathBlockPublicDto(x)).ToList();
    return new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = spec.Settings.MaxAttempts, passPercent = spec.Settings.PassPercent, attempt.TimeLimitSeconds, startedAt = attempt.StartedAt, startedAtUtc = attempt.StartedAt, spec.Settings.ShuffleBlocks, blocks };
}

static TaskSpec ReadTaskSpec(Assignment assignment)
{
    var root = JsonNode.Parse(string.IsNullOrWhiteSpace(assignment.TestsJson) ? "{}" : assignment.TestsJson!) as JsonObject ?? new JsonObject();
    return ParseTaskSpec(root);
}
static MathSpec ReadMathSpec(Assignment assignment)
{
    var root = JsonNode.Parse(string.IsNullOrWhiteSpace(assignment.TestsJson) ? "{}" : assignment.TestsJson!) as JsonObject ?? new JsonObject();
    return ParseMathSpec(root);
}
static async Task<IResult> SaveSpec(Guid assignmentId, JsonElement payload, TasksDbContext db, string kind)
{
    var assignment = await db.Assignments.FindAsync(assignmentId);
    if (assignment == null) return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
    var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
    NormalizeIds(node, kind == "test" ? "questions" : "blocks");
    assignment.TestsJson = node.ToJsonString(JsonOptions());
    assignment.Type = kind;
    assignment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(kind == "test" ? TaskSpecToJsonObject(ParseTaskSpec(node)) : MathSpecToJsonObject(ParseMathSpec(node)));
}
static void NormalizeIds(JsonObject root, string arrayName)
{
    if (root[arrayName] is not JsonArray arr) return;
    foreach (var item in arr.OfType<JsonObject>())
    {
        var raw = item["id"]?.GetValue<string>();
        if (!Guid.TryParse(raw, out var id) || id == Guid.Empty) item["id"] = Guid.NewGuid().ToString();
    }
}

static bool IsTestCorrect(TestQuestion q, TestAnswer? a)
{
    var type = q.Type.ToLowerInvariant();
    if (type is "single-choice" or "multi-choice") return SetEq(a?.SelectedOptionKeys ?? (a?.SelectedOptionKey == null ? [] : [a.SelectedOptionKey]), q.CorrectOptionKeys);
    if (type is "fill" or "text") return TextAccepted(a?.Text, q.AcceptedAnswers, q.CaseSensitive, q.Trim);
    return false;
}
static bool IsMathCorrect(MathBlock b, MathAnswer? a)
{
    var k = b.Kind.ToLowerInvariant();
    if (k is "single-choice" or "multi-choice") return SetEq(a?.SelectedOptionKeys ?? [], b.CorrectOptionKeys);
    if (k is "text" or "fill" or "formula" or "numeric") return TextAccepted(a?.Text, b.AcceptedAnswers, b.CaseSensitive, b.Trim, b.NumericTolerance);
    if (k is "order") return SeqEq(a?.OrderedItems ?? [], b.OrderItems);
    if (k is "match") return MatchEq(a?.MatchPairs ?? [], b.MatchPairs);
    return true;
}
static bool TextAccepted(string? value, List<string> accepted, bool caseSensitive = false, bool trim = true, double? tolerance = null)
{
    var v = value ?? string.Empty;
    if (trim) v = v.Trim();
    if (tolerance.HasValue && double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv))
    {
        return accepted.Any(x => double.TryParse((trim ? x.Trim() : x).Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var da) && Math.Abs(dv - da) <= tolerance.Value);
    }
    return accepted.Any(x => string.Equals(v, trim ? x.Trim() : x, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
}
static bool SetEq(IEnumerable<string> a, IEnumerable<string> b) => a.Select(x => x.Trim()).Where(x => x.Length > 0).OrderBy(x => x).SequenceEqual(b.Select(x => x.Trim()).Where(x => x.Length > 0).OrderBy(x => x), StringComparer.OrdinalIgnoreCase);
static bool SeqEq(IEnumerable<string> a, IEnumerable<string> b) => a.Select(x => x.Trim()).SequenceEqual(b.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
static bool MatchEq(IEnumerable<MatchPair> a, IEnumerable<MatchPair> b) => SetEq(a.Select(x => $"{x.LeftKey}->{x.RightKey}"), b.Select(x => $"{x.LeftKey}->{x.RightKey}"));
static bool IsTimeExpired(TaskAttempt a) => a.TimeLimitSeconds.HasValue && DateTimeOffset.UtcNow > a.StartedAt.AddSeconds(a.TimeLimitSeconds.Value + 5);
static int? TimeLimitFor(List<int?> limits, int attemptNumber) => attemptNumber >= 1 && attemptNumber <= limits.Count && limits[attemptNumber - 1].GetValueOrDefault() > 0 ? limits[attemptNumber - 1] : null;
static List<Guid> OrderedIds(IEnumerable<Guid> ids, bool shuffle, Guid seed) { var list = ids.ToList(); if (shuffle) Shuffle(list, seed); return list; }
static void Shuffle<T>(IList<T> list, Guid seed) { var rnd = new Random(BitConverter.ToInt32(seed.ToByteArray(), 0)); for (var i = list.Count - 1; i > 0; i--) { var j = rnd.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); } }
static async Task<bool> CanUserAccessAssignmentAsync(Assignment assignment, HttpContext http, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct)
{
    if (IsEditor(http, cfg)) return true;
    if (!assignment.IsVisible) return false;
    return await CanUserAccessCourseAsync(assignment.CourseId, http, cfg, clients, ct);
}

static async Task<bool> CanUserAccessCourseAsync(Guid courseId, HttpContext http, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct)
{
    if (IsEditor(http, cfg)) return true;
    var userId = TaskForgeRequestSecurity.UserId(http, cfg);
    if (!userId.HasValue) return false;
    var access = await LoadCourseAccessAsync(courseId, userId.Value, clients, cfg, ct);
    return access?.CanView == true;
}

static async Task<CourseAccessDto?> LoadCourseAccessAsync(Guid courseId, Guid userId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
{
    return await GetInternalAsync<CourseAccessDto>(clients, cfg, ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/access/{userId:D}", ct);
}

static async Task<T?> GetInternalAsync<T>(IHttpClientFactory httpFactory, IConfiguration cfg, string baseUrl, string path, CancellationToken ct)
{
    try
    {
        var client = httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + path);
        AddInternalKey(msg, cfg);
        using var resp = await client.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>(JsonOptions(), ct);
    }
    catch
    {
        return default;
    }
}

static Guid? RequireUser(HttpContext http, IConfiguration cfg) => TaskForgeRequestSecurity.UserId(http, cfg);
static IResult Unauthorized() => Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);
static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);
static long MaxImageUploadBytes(IConfiguration cfg) => cfg.GetValue<long?>("Files:MaxUploadBytes") ?? cfg.GetValue<long?>("Files:MaxImageUploadBytes") ?? 10L * 1024 * 1024;
static object ToDto(Assignment x, bool includeSensitive = false)
{
    var tests = includeSensitive ? ParseJson(x.TestsJson) : PublicTestsJson(x.TestsJson);
    return new
    {
        x.Id,
        x.CourseId,
        x.Title,
        x.Description,
        x.Type,
        x.Language,
        allowedLanguages = ParseCsv(x.AllowedLanguagesCsv, x.Language),
        x.StarterCode,
        tests,
        testCases = tests,
        testsJson = includeSensitive ? x.TestsJson : null,
        tags = x.Tags ?? string.Empty,
        difficulty = x.Difficulty,
        rating = x.Rating,
        isHidden = !x.IsVisible,
        isAiDraft = false,
        lifecycleStatus = x.IsVisible ? "published" : "draft",
        codeForbiddenCalls = includeSensitive ? ParseStringArrayJson(x.CodeForbiddenCallsJson) : Array.Empty<string>(),
        codeRequiredCalls = includeSensitive ? ParseStringArrayJson(x.CodeRequiredCallsJson) : Array.Empty<string>(),
        imageTestReferenceKey = includeSensitive ? JsonString(x.TestsJson, "imageTestReferenceKey") : null,
        imageTestSimilarityThreshold = JsonInt(x.TestsJson, "imageTestSimilarityThreshold", 90),
        x.IsVisible,
        x.Sort,
        canEdit = includeSensitive,
        isSolved = false,
        x.CreatedAt,
        x.UpdatedAt
    };
}

static AssignmentSummaryDto ToAssignmentSummaryDto(Assignment x) => new(
    x.Id,
    x.Id,
    x.CourseId,
    x.Title,
    x.Title,
    x.Type,
    x.Language,
    x.Rating,
    x.Difficulty,
    x.IsVisible,
    x.Sort);

static bool IsEditor(HttpContext http, IConfiguration cfg)
{
    var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
    return principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor");
}

static JsonElement? PublicTestsJson(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.SerializeToElement(root.EnumerateArray().Where(IsPublicTest).Select(SanitizePublicTest).ToArray(), JsonOptions());
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.SerializeToElement(publicTests.EnumerateArray().Select(SanitizePublicTest).ToArray(), JsonOptions());
            }
            foreach (var name in new[] { "testCases", "tests", "cases" })
            {
                if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.SerializeToElement(arr.EnumerateArray().Where(IsPublicTest).Select(SanitizePublicTest).ToArray(), JsonOptions());
                }
            }
            return null;
        }
    }
    catch { }
    return null;
}

static bool IsPublicTest(JsonElement t)
{
    if (t.ValueKind != JsonValueKind.Object) return false;
    if (BoolProp(t, "isHidden") || BoolProp(t, "hidden")) return false;
    return true;
}

static object SanitizePublicTest(JsonElement t)
{
    var input = StringProp(t, "input") ?? StringProp(t, "stdin") ?? string.Empty;
    var expected = StringProp(t, "expectedOutput") ?? StringProp(t, "expected") ?? StringProp(t, "stdout") ?? string.Empty;
    var threshold = IntProp(t, "threshold") ?? IntProp(t, "thresholdPercent") ?? 90;
    var hasExpectedImage = !string.IsNullOrWhiteSpace(StringProp(t, "expectedImageKey") ?? StringProp(t, "referenceKey") ?? StringProp(t, "imageKey") ?? StringProp(t, "imageTestReferenceKey") ?? StringProp(t, "expectedImageBase64") ?? StringProp(t, "referenceBase64") ?? StringProp(t, "imageBase64"));
    // Never expose judge storage keys, private URLs or inline image payloads to the student-facing DTO.
    return new { input, expectedOutput = expected, threshold, isHidden = false, hasExpectedImage };
}

static int? IntProp(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : null;
static bool BoolProp(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
static string? StringProp(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) ? v.ToString() : null;

static bool HasMeaningfulJsonText(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return false;
    try
    {
        using var doc = JsonDocument.Parse(text);
        return HasMeaningfulJsonValue(doc.RootElement);
    }
    catch { return true; }
}

static bool HasMeaningfulJsonElement(JsonElement? element) => element.HasValue && HasMeaningfulJsonValue(element.Value);

static bool HasMeaningfulJsonValue(JsonElement element) => element.ValueKind switch
{
    JsonValueKind.Array => element.GetArrayLength() > 0,
    JsonValueKind.Object => element.EnumerateObject().Any(),
    JsonValueKind.Null or JsonValueKind.Undefined => false,
    _ => true
};

static async Task<JsonObject> MergeAndMaterializeImageTestPayloadAsync(string? existingJson, AssignmentRequest request, Guid assignmentId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
{
    var node = MergeImageTestPayload(existingJson, request);
    if (assignmentId != Guid.Empty)
    {
        await MaterializeImageTestImagesAsync(node, assignmentId, clients, cfg, ct);
    }
    return node;
}

static async Task MaterializeImageTestImagesAsync(JsonObject root, Guid assignmentId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
{
    foreach (var arrayName in new[] { "testCases", "tests", "cases", "publicTests", "hiddenTests" })
    {
        if (root[arrayName] is not JsonArray arr) continue;
        var index = 0;
        foreach (var item in arr.OfType<JsonObject>())
        {
            index++;
            await MaterializeImageObjectAsync(item, assignmentId, index, clients, cfg, ct);
        }
    }

    var rootKey = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey");
    var rootBase64 = NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64");
    if (string.IsNullOrWhiteSpace(rootKey) && !string.IsNullOrWhiteSpace(rootBase64))
    {
        var decoded = DecodeImageBase64(rootBase64, NodeString(root, "referenceContentType") ?? NodeString(root, "expectedImageContentType") ?? "image/png");
        var uploaded = await UploadImageBytesToFilesApiAsync(clients, cfg, decoded.Bytes, NodeString(root, "referenceFileName") ?? NodeString(root, "expectedImageFileName") ?? $"reference-{assignmentId:N}{ExtensionForContentType(decoded.ContentType)}", decoded.ContentType, $"image-tests/reference/{assignmentId:N}", ct);
        root["imageTestReferenceKey"] = uploaded.Key;
        root["imageTestReferenceUrl"] = uploaded.PrivateUrl;
        root["referenceFileName"] = uploaded.FileName;
        root["referenceContentType"] = uploaded.ContentType;
        root["referenceSize"] = uploaded.Size;
    }
    RemoveInlineImageFields(root);
}

static async Task MaterializeImageObjectAsync(JsonObject o, Guid assignmentId, int index, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
{
    var key = NodeString(o, "expectedImageKey") ?? NodeString(o, "referenceKey") ?? NodeString(o, "imageKey") ?? NodeString(o, "imageTestReferenceKey");
    if (!string.IsNullOrWhiteSpace(key))
    {
        o["expectedImageKey"] = key;
        o["expectedImageUrl"] = PrivateFileUrl(key);
        RemoveInlineImageFields(o);
        return;
    }

    var raw = NodeString(o, "expectedImageBase64") ?? NodeString(o, "referenceBase64") ?? NodeString(o, "imageBase64");
    if (string.IsNullOrWhiteSpace(raw)) return;

    var decoded = DecodeImageBase64(raw, NodeString(o, "expectedImageContentType") ?? NodeString(o, "referenceContentType") ?? "image/png");
    var uploaded = await UploadImageBytesToFilesApiAsync(clients, cfg, decoded.Bytes, NodeString(o, "expectedImageFileName") ?? NodeString(o, "referenceFileName") ?? $"case-{index}{ExtensionForContentType(decoded.ContentType)}", decoded.ContentType, $"image-tests/reference/{assignmentId:N}", ct);
    o["expectedImageKey"] = uploaded.Key;
    o["expectedImageUrl"] = uploaded.PrivateUrl;
    o["expectedImageContentType"] = uploaded.ContentType;
    o["expectedImageFileName"] = uploaded.FileName;
    o["expectedImageSize"] = uploaded.Size;
    RemoveInlineImageFields(o);
}

static void RemoveInlineImageFields(JsonObject o)
{
    o.Remove("expectedImageBase64");
    o.Remove("referenceBase64");
    o.Remove("imageBase64");
    o.Remove("expectedBase64");
}

static (byte[] Bytes, string ContentType) DecodeImageBase64(string raw, string fallbackContentType)
{
    var text = (raw ?? string.Empty).Trim();
    var contentType = string.IsNullOrWhiteSpace(fallbackContentType) ? "image/png" : fallbackContentType;
    if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        var comma = text.IndexOf(',');
        if (comma > 0)
        {
            var meta = text[5..comma];
            var semi = meta.IndexOf(';');
            if (semi >= 0) meta = meta[..semi];
            if (!string.IsNullOrWhiteSpace(meta)) contentType = meta;
            text = text[(comma + 1)..];
        }
    }
    return (Convert.FromBase64String(text), contentType);
}

static string ExtensionForContentType(string? contentType) => (contentType ?? string.Empty).ToLowerInvariant() switch
{
    "image/jpeg" or "image/jpg" => ".jpg",
    "image/webp" => ".webp",
    "image/gif" => ".gif",
    "image/bmp" => ".bmp",
    "image/svg+xml" => ".svg",
    _ => ".png"
};

static JsonObject MergeImageTestPayload(string? existingJson, AssignmentRequest request)
{
    JsonObject node;
    try
    {
        var parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(existingJson) ? "{}" : existingJson!);
        if (parsed is JsonObject obj)
        {
            node = obj;
        }
        else if (parsed is JsonArray arr)
        {
            node = new JsonObject { ["testCases"] = arr.DeepClone() };
        }
        else
        {
            node = new JsonObject();
        }
    }
    catch
    {
        node = new JsonObject();
    }
    if (!string.IsNullOrWhiteSpace(request.ImageTestReferenceKey)) node["imageTestReferenceKey"] = request.ImageTestReferenceKey;
    if (request.ImageTestSimilarityThreshold.HasValue) node["imageTestSimilarityThreshold"] = Math.Clamp(request.ImageTestSimilarityThreshold.Value, 0, 100);
    return node;
}

static string Clean(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

static string NormalizeAssignmentType(string? value)
{
    var s = (value ?? string.Empty).Trim().ToLowerInvariant();
    return s switch
    {
        "code" or "code_test" or "codetest" or "programming" or "programming-test" => "code-test",
        "image" or "image_test" or "imagetest" or "drawing" or "drawing-test" => "image-test",
        "quiz" or "task-test" or "multiple-choice" => "test",
        "math-test" or "math_task" or "math-task" => "math",
        "image-test" or "code-test" or "test" or "math" => s,
        _ => "code-test"
    };
}

static async Task<Assignment> BuildAssignmentEntityAsync(Guid courseId, AssignmentRequest request, int sort, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
{
    var type = NormalizeAssignmentType(request.Type);
    var testsJson = request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases);
    testsJson = NormalizeSpecJsonForStorage(testsJson, type);
    var assignment = new Assignment
    {
        CourseId = courseId,
        Title = Clean(request.Title, "Новое задание"),
        Description = request.Description,
        Type = type,
        Language = NormalizeLanguage(request.Language) ?? (type == "image-test" ? "python" : "csharp"),
        AllowedLanguagesCsv = NormalizeLanguagesCsv(request.AllowedLanguages),
        Tags = request.Tags,
        Difficulty = Math.Clamp(request.Difficulty ?? 1, 1, 3),
        Rating = Math.Max(0, request.Rating ?? 1),
        StarterCode = request.StarterCode,
        TestsJson = type == "image-test" ? null : testsJson,
        CodeForbiddenCallsJson = StringArrayJson(request.CodeForbiddenCalls),
        CodeRequiredCallsJson = StringArrayJson(request.CodeRequiredCalls),
        IsVisible = request.IsVisible ?? !(request.IsHidden ?? false),
        Sort = sort
    };

    if (type == "image-test")
    {
        assignment.TestsJson = (await MergeAndMaterializeImageTestPayloadAsync(testsJson, request, assignment.Id, clients, cfg, ct)).ToJsonString(JsonOptions());
    }

    return assignment;
}

static string? NormalizeSpecJsonForStorage(string? testsJson, string type)
{
    if (string.IsNullOrWhiteSpace(testsJson)) return testsJson;
    if (type != "test" && type != "math") return testsJson;
    try
    {
        var node = JsonNode.Parse(testsJson) as JsonObject;
        if (node == null) return testsJson;
        NormalizeIds(node, type == "test" ? "questions" : "blocks");
        return node.ToJsonString(JsonOptions());
    }
    catch
    {
        return testsJson;
    }
}

static IEnumerable<JsonElement> ExtractAssignmentImportItems(JsonElement root)
{
    if (root.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
        yield break;
    }

    if (root.ValueKind != JsonValueKind.Object) yield break;

    foreach (var name in new[] { "assignments", "items", "tasks" })
    {
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) yield return item;
            }
            yield break;
        }
    }

    if (root.TryGetProperty("assignment", out var nested) && nested.ValueKind == JsonValueKind.Object)
    {
        yield return nested;
        yield break;
    }

    yield return root;
}

static AssignmentRequest AssignmentRequestFromJson(JsonElement source)
{
    if (source.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("ожидался JSON-объект");
    }

    var type = NormalizeAssignmentType(FirstString(source, "type", "kind", "assignmentType"));
    var tests = PickTestsElement(source, type);

    return new AssignmentRequest(
        FirstString(source, "title", "name", "assignmentTitle"),
        FirstString(source, "description", "statement", "condition", "body", "prompt"),
        type,
        FirstString(source, "language", "defaultLanguage"),
        FirstStringList(source, "allowedLanguages", "languages"),
        FirstString(source, "tags"),
        FirstInt(source, "difficulty", "level"),
        FirstInt(source, "rating", "score", "points"),
        FirstString(source, "starterCode", "templateCode", "initialCode"),
        FirstString(source, "testsJson"),
        tests,
        tests,
        FirstStringList(source, "codeForbiddenCalls", "forbiddenCalls", "forbidden"),
        FirstStringList(source, "codeRequiredCalls", "requiredCalls", "required"),
        FirstBool(source, "isVisible", "visible"),
        FirstBool(source, "isHidden", "hidden"),
        FirstString(source, "imageTestReferenceKey", "referenceKey", "expectedImageKey"),
        FirstInt(source, "imageTestSimilarityThreshold", "similarityThreshold", "threshold")
    );
}

static JsonElement? PickTestsElement(JsonElement source, string type)
{
    if (type == "test")
    {
        foreach (var name in new[] { "testSpec", "taskTest", "quiz", "tests", "testCases", "spec" })
        {
            if (source.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return v.Clone();
        }
        if (source.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            return WrapSpec(source, "settings", "questions");
        }
    }

    if (type == "math")
    {
        foreach (var name in new[] { "mathSpec", "mathTask", "math", "tests", "testCases", "spec" })
        {
            if (source.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return v.Clone();
        }
        if (source.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            return WrapSpec(source, "settings", "blocks");
        }
    }

    if (type == "image-test")
    {
        foreach (var name in new[] { "imageSpec", "imageTest", "tests", "testCases", "cases", "publicTests", "hiddenTests" })
        {
            if (source.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return name is "publicTests" or "hiddenTests" ? WrapImageTests(source) : v.Clone();
            }
        }
    }

    foreach (var name in new[] { "tests", "testCases", "cases", "publicTests", "hiddenTests" })
    {
        if (source.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return name is "publicTests" or "hiddenTests" ? WrapCodeTests(source) : v.Clone();
        }
    }

    return null;
}

static JsonElement WrapSpec(JsonElement source, string settingsName, string arrayName)
{
    var node = new JsonObject();
    if (source.TryGetProperty(settingsName, out var settings) && settings.ValueKind == JsonValueKind.Object)
    {
        node[settingsName] = JsonNode.Parse(settings.GetRawText());
    }
    if (source.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
    {
        node[arrayName] = JsonNode.Parse(arr.GetRawText());
    }
    return JsonSerializer.SerializeToElement(node, JsonOptions());
}

static JsonElement WrapCodeTests(JsonElement source)
{
    var node = new JsonObject();
    if (source.TryGetProperty("publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array) node["publicTests"] = JsonNode.Parse(publicTests.GetRawText());
    if (source.TryGetProperty("hiddenTests", out var hiddenTests) && hiddenTests.ValueKind == JsonValueKind.Array) node["hiddenTests"] = JsonNode.Parse(hiddenTests.GetRawText());
    return JsonSerializer.SerializeToElement(node, JsonOptions());
}

static JsonElement WrapImageTests(JsonElement source)
{
    var node = JsonNode.Parse(WrapCodeTests(source).GetRawText()) as JsonObject ?? new JsonObject();
    foreach (var name in new[] { "imageTestReferenceKey", "imageTestSimilarityThreshold", "referenceKey", "threshold" })
    {
        if (source.TryGetProperty(name, out var v)) node[name] = JsonNode.Parse(v.GetRawText());
    }
    return JsonSerializer.SerializeToElement(node, JsonOptions());
}

static IEnumerable<string> ValidateImportedAssignment(AssignmentRequest request, int index)
{
    var title = (request.Title ?? string.Empty).Trim();
    if (title.Length == 0) yield return "title обязателен.";
    if (title.Length > 200) yield return "title не должен быть длиннее 200 символов.";
    var type = NormalizeAssignmentType(request.Type);
    if (!new[] { "code-test", "image-test", "test", "math" }.Contains(type, StringComparer.OrdinalIgnoreCase)) yield return "type должен быть code-test, image-test, test или math.";
    if (request.Difficulty.HasValue && (request.Difficulty.Value < 1 || request.Difficulty.Value > 3)) yield return "difficulty должен быть 1, 2 или 3.";
    if (request.Rating.HasValue && request.Rating.Value < 0) yield return "rating не может быть отрицательным.";
    if ((type == "code-test" || type == "image-test") && string.IsNullOrWhiteSpace(request.TestsJson) && !request.Tests.HasValue && !request.TestCases.HasValue) yield return "для code-test/image-test желательно указать testCases/tests.";
    if ((type == "test" || type == "math") && string.IsNullOrWhiteSpace(request.TestsJson) && !request.Tests.HasValue && !request.TestCases.HasValue) yield return "для test/math нужно указать spec/questions/blocks.";
}

static string? FirstString(JsonElement source, params string[] names)
{
    foreach (var name in names)
    {
        if (!source.TryGetProperty(name, out var v)) continue;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
        if (name == "tags" && v.ValueKind == JsonValueKind.Array)
        {
            var tags = v.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => x.Length > 0).ToArray();
            return tags.Length == 0 ? null : string.Join(", ", tags);
        }
    }
    return null;
}

static int? FirstInt(JsonElement source, params string[] names)
{
    foreach (var name in names)
    {
        if (!source.TryGetProperty(name, out var v)) continue;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
    }
    return null;
}

static bool? FirstBool(JsonElement source, params string[] names)
{
    foreach (var name in names)
    {
        if (!source.TryGetProperty(name, out var v)) continue;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
    }
    return null;
}

static List<string>? FirstStringList(JsonElement source, params string[] names)
{
    foreach (var name in names)
    {
        if (!source.TryGetProperty(name, out var v)) continue;
        if (v.ValueKind == JsonValueKind.Array)
        {
            var list = v.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return list.Count == 0 ? null : list;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            var list = (v.GetString() ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return list.Count == 0 ? null : list;
        }
    }
    return null;
}

static string? NormalizeLanguage(string? value)
{
    var s = (value ?? string.Empty).Trim().ToLowerInvariant();
    return s switch
    {
        "c#" or "cs" or "csharp" => "csharp",
        "c++" or "cpp" or "g++" or "gcc" or "cxx" => "cpp",
        "py" or "python" or "python3" => "python",
        "js" or "node" or "nodejs" or "node.js" or "javascript" => "javascript",
        "pas" or "pascal" or "pascalabc" or "pascalabcnet" or "pabc" => "pascal",
        "java" => "java",
        _ => null
    };
}
static string[] SupportedCodeLanguages() => ["cpp", "python", "csharp", "javascript", "pascal", "java"];
static List<string> NormalizeLanguageList(IEnumerable<string>? values)
{
    var list = (values ?? []).Select(NormalizeLanguage).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    return list.Count == 0 ? [] : list.Where(x => SupportedCodeLanguages().Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
}
static string? NormalizeLanguagesCsv(IEnumerable<string>? values)
{
    var list = NormalizeLanguageList(values);
    return list.Count == 0 ? null : string.Join(',', list);
}
static string[] ParseCsv(string? csv, string fallback)
{
    var list = NormalizeLanguageList((csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (list.Count > 0) return list.ToArray();
    var one = NormalizeLanguage(fallback) ?? "csharp";
    return [one];
}
static string? StringArrayJson(IEnumerable<string>? values)
{
    if (values == null) return null;
    var list = values.Select(x => (x ?? string.Empty).Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    return list.Length == 0 ? null : JsonSerializer.Serialize(list, JsonOptions());
}
static string[] ParseStringArrayJson(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return [];
    try
    {
        return JsonSerializer.Deserialize<string[]>(json, JsonOptions())?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    }
    catch { return []; }
}
static string? RawJson(JsonElement? value) => value.HasValue ? value.Value.GetRawText() : null;
static object? ParseJson(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return json; } }
static JsonElement? ParseJsonElement(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return null; } }
static object JsonPropArray(object? json, string name) { if (json is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array) return p; return Array.Empty<object>(); }
static Guid GuidProp(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && Guid.TryParse(p.ToString(), out var id) ? id : Guid.Empty;
static List<Guid> ParseGuidList(string? json) { try { return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions()) ?? []; } catch { return []; } }
static string? JsonString(string? json, string name) { var e = ParseJsonElement(json); return e.HasValue && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(name, out var p) ? p.ToString() : null; }
static int JsonInt(string? json, string name, int fallback) { var e = ParseJsonElement(json); return e.HasValue && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback; }
static List<TestAnswer> AnswersArray(JsonElement payload, string name)
{
    if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
    return arr.EnumerateArray().Select(x => new TestAnswer(GuidProp(x, "questionId"), x.TryGetProperty("selectedOptionKey", out var s) ? s.ToString() : null, Strings(x, "selectedOptionKeys"), x.TryGetProperty("text", out var t) ? t.ToString() : null)).ToList();
}
static List<MathAnswer> MathAnswersArray(JsonElement payload, string name)
{
    if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
    return arr.EnumerateArray().Select(x => new MathAnswer(GuidProp(x, "blockId"), x.TryGetProperty("text", out var t) ? t.ToString() : null, Strings(x, "selectedOptionKeys"), Strings(x, "orderedItems"), MatchPairs(x, "matchPairs"))).ToList();
}
static List<string> Strings(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : [];
static List<MatchPair> MatchPairs(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => new MatchPair(x.TryGetProperty("leftKey", out var l) ? l.ToString() : string.Empty, x.TryGetProperty("rightKey", out var r) ? r.ToString() : string.Empty)).ToList() : [];


static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

static TaskSpec ParseTaskSpec(JsonObject root)
{
    var settings = root["settings"] as JsonObject ?? new JsonObject();
    var questions = root["questions"] as JsonArray ?? new JsonArray();
    return new TaskSpec(
        new TestSettings(
            Int(settings, "maxAttempts", 1),
            Math.Clamp(Int(settings, "passPercent", 60), 0, 100),
            Bool(settings, "shuffleQuestions", true),
            Bool(settings, "shuffleAnswers", true),
            Bool(settings, "allowReview", true),
            IntList(settings, "attemptTimeLimitsSeconds")),
        questions.OfType<JsonObject>().Select(ParseQuestion).OrderBy(x => x.Order).ToList());
}

static JsonObject TaskSpecToJsonObject(TaskSpec spec) => new()
{
    ["settings"] = JsonSerializer.SerializeToNode(spec.Settings, JsonOptions()),
    ["questions"] = JsonSerializer.SerializeToNode(spec.Questions, JsonOptions())
};

static TestQuestion ParseQuestion(JsonObject q) => new(
    GuidV(q, "id"),
    Int(q, "order", 0),
    Str(q, "type", "single-choice"),
    Str(q, "prompt", ""),
    Options(q),
    StringList(q, "correctOptionKeys"),
    StringList(q, "acceptedAnswers"),
    Bool(q, "caseSensitive", false),
    Bool(q, "trim", true));

static object TestQuestionPublicDto(TestQuestion question, bool shuffleAnswers, Guid seed)
{
    var opts = question.Options.ToList();
    if (shuffleAnswers) Shuffle(opts, seed);
    return new
    {
        question.Id,
        question.Order,
        question.Type,
        question.Prompt,
        options = question.Type is "single-choice" or "multi-choice" ? opts : null
    };
}

static JsonObject TestQuestionReviewNode(TestQuestion question, TestAnswer? answer, bool isCorrect) => new()
{
    ["id"] = question.Id.ToString(),
    ["order"] = question.Order,
    ["type"] = question.Type,
    ["prompt"] = question.Prompt,
    ["options"] = JsonSerializer.SerializeToNode(question.Options, JsonOptions()),
    ["correctOptionKeys"] = JsonSerializer.SerializeToNode(question.CorrectOptionKeys, JsonOptions()),
    ["acceptedAnswers"] = JsonSerializer.SerializeToNode(question.AcceptedAnswers, JsonOptions()),
    ["userAnswer"] = JsonSerializer.SerializeToNode(answer, JsonOptions()),
    ["isCorrect"] = isCorrect
};

static MathSpec ParseMathSpec(JsonObject root)
{
    var settings = root["settings"] as JsonObject ?? new JsonObject();
    var blocks = root["blocks"] as JsonArray ?? new JsonArray();
    return new MathSpec(
        new MathSettings(
            Int(settings, "maxAttempts", 1),
            Math.Clamp(Int(settings, "passPercent", 60), 0, 100),
            Bool(settings, "shuffleBlocks", false),
            Bool(settings, "allowReview", true),
            IntList(settings, "attemptTimeLimitsSeconds")),
        blocks.OfType<JsonObject>().Select(ParseBlock).OrderBy(x => x.Order).ToList());
}

static JsonObject MathSpecToJsonObject(MathSpec spec) => new()
{
    ["settings"] = JsonSerializer.SerializeToNode(spec.Settings, JsonOptions()),
    ["blocks"] = JsonSerializer.SerializeToNode(spec.Blocks, JsonOptions())
};

static MathBlock ParseBlock(JsonObject b) => new(
    GuidV(b, "id"),
    Int(b, "order", 0),
    Str(b, "kind", "info"),
    Str(b, "prompt", ""),
    b["promptContentJson"]?.GetValue<string>(),
    Int(b, "score", 1),
    Bool(b, "isRequired", true),
    Options(b),
    StringList(b, "correctOptionKeys"),
    StringList(b, "acceptedAnswers"),
    Bool(b, "caseSensitive", false),
    Bool(b, "trim", true),
    DoubleN(b, "numericTolerance"),
    StringList(b, "orderItems"),
    Options(b, "matchLeftItems"),
    Options(b, "matchRightItems"),
    MatchPairList(b, "matchPairs"));

static object MathBlockPublicDto(MathBlock block) => new
{
    block.Id,
    block.Order,
    block.Kind,
    block.Prompt,
    block.PromptContentJson,
    block.Score,
    block.IsRequired,
    options = block.Options,
    orderItems = block.OrderItems,
    matchLeftItems = block.MatchLeftItems,
    matchRightItems = block.MatchRightItems
};

static JsonObject MathBlockReviewNode(MathBlock block, MathAnswer? answer, bool isCorrect) => new()
{
    ["id"] = block.Id.ToString(),
    ["order"] = block.Order,
    ["kind"] = block.Kind,
    ["prompt"] = block.Prompt,
    ["promptContentJson"] = block.PromptContentJson,
    ["score"] = block.Score,
    ["isRequired"] = block.IsRequired,
    ["options"] = JsonSerializer.SerializeToNode(block.Options, JsonOptions()),
    ["correctOptionKeys"] = JsonSerializer.SerializeToNode(block.CorrectOptionKeys, JsonOptions()),
    ["acceptedAnswers"] = JsonSerializer.SerializeToNode(block.AcceptedAnswers, JsonOptions()),
    ["orderItems"] = JsonSerializer.SerializeToNode(block.OrderItems, JsonOptions()),
    ["matchPairs"] = JsonSerializer.SerializeToNode(block.MatchPairs, JsonOptions()),
    ["userAnswer"] = JsonSerializer.SerializeToNode(answer, JsonOptions()),
    ["isCorrect"] = isCorrect
};

static int Int(JsonObject o, string n, int d) => o[n] is JsonValue v && int.TryParse(v.ToString(), out var x) ? x : d;
static bool Bool(JsonObject o, string n, bool d) => o[n] is JsonValue v && bool.TryParse(v.ToString(), out var x) ? x : d;
static string Str(JsonObject o, string n, string d) => o[n]?.GetValue<string>() ?? d;
static Guid GuidV(JsonObject o, string n) => Guid.TryParse(o[n]?.ToString(), out var id) && id != Guid.Empty ? id : Guid.NewGuid();
static double? DoubleN(JsonObject o, string n) => o[n] is JsonValue v && double.TryParse(v.ToString().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : null;
static List<string> StringList(JsonObject o, string n) => o[n] is JsonArray a ? a.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToList() : [];
static List<int?> IntList(JsonObject o, string n) => o[n] is JsonArray a ? a.Select(x => int.TryParse(x?.ToString(), out var v) && v > 0 ? (int?)v : null).ToList() : [];
static List<Option> Options(JsonObject o, string n = "options") => o[n] is JsonArray a ? a.OfType<JsonObject>().Select(x => new Option(Str(x, "key", Guid.NewGuid().ToString("N")[..4]), Str(x, "text", ""))).ToList() : [];
static List<MatchPair> MatchPairList(JsonObject o, string n) => o[n] is JsonArray a ? a.OfType<JsonObject>().Select(x => new MatchPair(Str(x, "leftKey", ""), Str(x, "rightKey", ""))).ToList() : [];

static async Task<string> LoadCourseTitleAsync(Guid courseId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    if (courseId == Guid.Empty) return "Курс";
    try
    {
        var client = httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "EducationApi", "http://education-api:8080")}/api/internal/courses/metadata")
        {
            Content = JsonContent.Create(new CourseIdsRequest(new[] { courseId }), options: JsonOptions())
        };
        AddInternalKey(msg, cfg);
        using var resp = await client.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) return "Курс";
        var rows = await resp.Content.ReadFromJsonAsync<List<CourseSummaryDto>>(JsonOptions(), ct) ?? new List<CourseSummaryDto>();
        var row = rows.FirstOrDefault();
        return string.IsNullOrWhiteSpace(row?.Title ?? row?.CourseTitle) ? "Курс" : (row!.Title ?? row.CourseTitle)!;
    }
    catch { return "Курс"; }
}

static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(1000).ToArray();
    if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
    TaskForgeDebugTrace.UserSummaryRequest("tasks-api", "identity-api", ids);
    try
    {
        var client = httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080")}/api/internal/users/summaries")
        {
            Content = JsonContent.Create(new UserIdsRequest(ids), options: JsonOptions())
        };
        AddInternalKey(msg, cfg);
        using var resp = await client.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var empty = new Dictionary<Guid, UserSummaryDto>();
            TaskForgeDebugTrace.UserSummaryResponse("tasks-api", "identity-api", ids, empty);
            return empty;
        }
        var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
        var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
        TaskForgeDebugTrace.UserSummaryResponse("tasks-api", "identity-api", ids, map);
        return map;
    }
    catch
    {
        var empty = new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryResponse("tasks-api", "identity-api", ids, empty);
        return empty;
    }
}
static string UserLabel(UserSummaryDto? user)
{
    var name = (user?.DisplayName ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(name) && !LooksLikeEmail(name)) return name;
    var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    if (!string.IsNullOrWhiteSpace(full)) return full;
    var masked = (user?.MaskedEmail ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(masked)) return masked;
    return "Пользователь";
}
static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');
static double Percent(int num, int den) => den <= 0 ? 0 : Math.Round(num * 100.0 / den, 1);

public sealed record CourseAccessDto(Guid CourseId, Guid UserId, bool CanView, bool CanEdit, bool IsPublic);
public sealed record AssignmentAccessDto(Guid AssignmentId, Guid CourseId, Guid UserId, bool CanView, bool CanSubmit, bool IsVisible, bool CanEdit);
public sealed record AssignmentIdsRequest(Guid[]? AssignmentIds);
public sealed record AssignmentSummaryDto(Guid Id, Guid AssignmentId, Guid CourseId, string Title, string AssignmentTitle, string Type, string Language, int Rating, int Difficulty, bool IsVisible, int Sort);
public sealed record UserIdsRequest(Guid[] UserIds);
public sealed record CourseIdsRequest(Guid[]? CourseIds);
public sealed class CourseSummaryDto
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public string? Title { get; set; }
    public string? CourseTitle { get; set; }
}
public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Email { get; set; }
    public string? MaskedEmail { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = MaskedEmail;
    }
}
public sealed record ActivityLeaderboardRequest(Guid? CourseId, int? Days, Guid[]? UserIds);
public sealed record AssignmentRequest(string? Title, string? Description, string? Type, string? Language, List<string>? AllowedLanguages, string? Tags, int? Difficulty, int? Rating, string? StarterCode, string? TestsJson, JsonElement? Tests, JsonElement? TestCases, List<string>? CodeForbiddenCalls, List<string>? CodeRequiredCalls, bool? IsVisible, bool? IsHidden, string? ImageTestReferenceKey, int? ImageTestSimilarityThreshold);
public sealed record ImageCodeRequest(string? Language, string? Code, string? Input, int? TimeoutSeconds);
public sealed record ImageTestCaseSpec(string Name, string Input, string ExpectedOutput, string? ExpectedImageBase64, string? ExpectedImageKey, int Threshold, bool IsHidden, string ExpectedImageContentType, string ExpectedImageFileName)
{
    public string? ExpectedImageUrl => !string.IsNullOrWhiteSpace(ExpectedImageKey)
        ? $"/api/private-files/{Uri.EscapeDataString(ExpectedImageKey)}"
        : (!string.IsNullOrWhiteSpace(ExpectedImageBase64) ? $"data:{ExpectedImageContentType};base64,{ExpectedImageBase64}" : null);
}
public sealed record UploadedFileDto(string Key, string? PrivateUrl, string ContentType, string FileName, long Size);
public sealed record LoadedImageBytes(byte[] Bytes, string? ContentType, string? FileName);
public sealed record ImageCaseResult(int Index, string Name, string Input, string? ExpectedOutput, string? ActualOutput, bool StdoutPassed, bool ImagePassed, bool Passed, double Similarity, double SimilarityPercent, int Threshold, int ThresholdPercent, bool IsHidden, string? ReferenceUrl, string? SubmittedUrl, string? Stderr, JsonElement? Analyzer);
public sealed record AnalyzerRequest(string Language, string Source, object? ExtraForbidden, string[]? ForbiddenCalls, string[]? RequiredCalls);
public sealed record InternalImageSolutionRequest(Guid UserId, Guid AssignmentId, string? Language, string? Code, int SimilarityPercent, bool Passed, JsonElement? Result);
public sealed record SortRequest(int Sort);
public sealed record PositionRequest(int? Position, Guid? AfterAssignmentId);
public sealed record VisibilityRequest(bool IsVisible);
public sealed record TestAnswer(Guid QuestionId, string? SelectedOptionKey, List<string>? SelectedOptionKeys, string? Text);
public sealed record MathAnswer(Guid BlockId, string? Text, List<string>? SelectedOptionKeys, List<string>? OrderedItems, List<MatchPair>? MatchPairs);
public sealed record MatchPair(string LeftKey, string RightKey);
public sealed record TestSettings(int MaxAttempts, int PassPercent, bool ShuffleQuestions, bool ShuffleAnswers, bool AllowReview, List<int?> AttemptTimeLimitsSeconds);
public sealed record TestQuestion(Guid Id, int Order, string Type, string Prompt, List<Option> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool Trim);
public sealed record Option(string Key, string Text);
public sealed record TaskSpec(TestSettings Settings, List<TestQuestion> Questions);
public sealed record MathSettings(int MaxAttempts, int PassPercent, bool ShuffleBlocks, bool AllowReview, List<int?> AttemptTimeLimitsSeconds);
public sealed record MathBlock(Guid Id, int Order, string Kind, string Prompt, string? PromptContentJson, int Score, bool IsRequired, List<Option> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool Trim, double? NumericTolerance, List<string> OrderItems, List<Option> MatchLeftItems, List<Option> MatchRightItems, List<MatchPair> MatchPairs);
public sealed record MathSpec(MathSettings Settings, List<MathBlock> Blocks);


internal static class TaskForgeApiRateLimiters
{
    private static readonly SlidingWindowRateLimiter Limiter = new();
    public static bool Allow(string bucket, string key)
    {
        var (limit, window) = bucket switch
        {
            "task-submit" => (40, TimeSpan.FromMinutes(5)),
            "image-test" => (20, TimeSpan.FromMinutes(5)),
            _ => (60, TimeSpan.FromMinutes(1))
        };
        return Limiter.Allow(key, limit, window);
    }
}

internal sealed class SlidingWindowRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<long>> _hits = new();
    public bool Allow(string key, int limit, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var min = now - (long)window.TotalMilliseconds;
        var queue = _hits.GetOrAdd(key, _ => new Queue<long>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < min) queue.Dequeue();
            if (queue.Count >= limit) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
