using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("solutions-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "solutions-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<SolutionsDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("solutions-api");

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<SolutionsDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for SolutionsDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for SolutionsDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    await ensureScope.ServiceProvider.GetRequiredService<SolutionsDbContext>().Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("solutions");

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-solutions-api" }));
app.MapGet("/health/ready", async (SolutionsDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-solutions-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-solutions-api", database = "taskforge_solutions", status = "solutions microservice active" }));
app.MapGet("/api/solutions/api/schema-owner", () => Results.Ok(new { database = "taskforge_solutions", ownedEntities = new[] { "Submission", "UserRating", "Badge", "UserBadge", "UserQuotaBucket", "UserImageTaskSolution" } }));

app.MapPost("/api/assignments/{assignmentId:guid}/submit", async (Guid assignmentId, SubmitRequest request, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var userId = CurrentUserId(http, cfg);
    if (userId == null) return Unauthorized();
    var canRevealHidden = IsEditor(http, cfg);

    var language = NormalizeLanguage(request.Language) ?? "csharp";
    var code = request.Code ?? string.Empty;
    if (string.IsNullOrWhiteSpace(code))
    {
        return Problem(400, "SOLUTION_CODE_REQUIRED", "solutions.validation", "Нельзя отправить пустое решение.");
    }

    var sub = new SolutionSubmission
    {
        AssignmentId = assignmentId,
        UserId = userId,
        Language = language,
        Code = code,
        Status = "Preparing",
        Score = 0,
        ResultJson = JsonSerializer.Serialize(new { verdict = "Preparing", message = "Решение принято и готовится к проверке." }, JsonOptions())
    };
    db.Submissions.Add(sub);
    await db.SaveChangesAsync(ct);

    var spec = await LoadJudgeSpecAsync(assignmentId, cfg, httpFactory, ct);
    if (spec == null)
    {
        ApplyLocalVerdict(sub, new JudgeRunResult(
            "JudgeUnavailable",
            0,
            false,
            false,
            "Не удалось получить тесты задания из tasks-api. Проверьте, что tasks-api доступен и внутренний ключ совпадает.",
            null,
            null,
            false));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToSubmitDto(sub, canRevealHidden));
    }

    if (!IsAllowedLanguage(language, spec))
    {
        ApplyLocalVerdict(sub, new JudgeRunResult(
            "LanguageNotAllowed",
            0,
            false,
            false,
            $"Язык {language} не разрешён для этого задания.",
            null,
            CloneJson(JsonSerializer.Serialize(new { language, allowedLanguages = EffectiveAllowedLanguages(spec) }, JsonOptions())),
            false));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToSubmitDto(sub, canRevealHidden));
    }
    var tests = ExtractTests(spec);
    if (tests.Length == 0)
    {
        ApplyLocalVerdict(sub, new JudgeRunResult(
            "NoTestsConfigured",
            0,
            false,
            false,
            "Для задания не настроены тесты, поэтому решение не может быть зачтено автоматически.",
            null,
            null,
            false));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToSubmitDto(sub, canRevealHidden));
    }

    var enqueue = await EnqueueExecutionJobAsync(sub.Id, assignmentId, userId.Value, language, code, request.Input, tests, spec, cfg, httpFactory, ct);
    if (!enqueue.Created)
    {
        ApplyLocalVerdict(sub, new JudgeRunResult(
            "JudgeUnavailable",
            0,
            false,
            false,
            enqueue.Message ?? "Execution pipeline временно недоступен.",
            null,
            enqueue.Raw,
            false));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToSubmitDto(sub, canRevealHidden));
    }

    sub.Status = "Queued";
    sub.Score = 0;
    sub.ResultJson = JsonSerializer.Serialize(new
    {
        verdict = "Queued",
        status = "Queued",
        pending = true,
        executionJobId = enqueue.JobId,
        message = "Решение поставлено в очередь проверки. Результат появится после обработки execution-worker."
    }, JsonOptions());
    await db.SaveChangesAsync(ct);

    var final = await WaitForTerminalSubmissionAsync(db, sub.Id, cfg, ct);
    return Results.Ok(ToSubmitDto(final ?? sub, canRevealHidden));
});

app.MapPost("/api/internal/solutions/submissions/{submissionId:guid}/verdict", async (Guid submissionId, SolutionVerdictRequest request, SolutionsDbContext db, CancellationToken ct) =>
{
    var sub = await db.Submissions.FirstOrDefaultAsync(x => x.Id == submissionId, ct);
    if (sub == null) return Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });

    var previous = sub.Status;
    var isTerminalBefore = IsTerminalVerdict(previous);
    sub.Status = CleanVerdict(request.Verdict);
    sub.Score = Math.Clamp(request.Score, 0, 100);
    sub.ResultJson = request.Result.HasValue
        ? request.Result.Value.GetRawText()
        : JsonSerializer.Serialize(new { verdict = sub.Status, score = sub.Score, message = request.Message }, JsonOptions());

    if (!isTerminalBefore && sub.UserId.HasValue && IsTerminalVerdict(sub.Status))
    {
        await AddRating(db, sub.UserId.Value, sub.Score, accepted: string.Equals(sub.Status, "Accepted", StringComparison.OrdinalIgnoreCase));
    }

    await db.SaveChangesAsync(ct);
    return Results.Ok(ToDto(sub, includeSensitiveResult: true));
});

app.MapGet("/api/assignments/{assignmentId:guid}/top-solutions", async (Guid assignmentId, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, int top = 20, CancellationToken ct = default) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();

    var limit = Math.Clamp(top, 1, 100);
    var canViewCode = IsEditor(http, cfg) || await db.Submissions.AsNoTracking().AnyAsync(x => x.AssignmentId == assignmentId && x.UserId == uid.Value && x.Status == "Accepted", ct);
    var rows = await db.Submissions.AsNoTracking()
        .Where(x => x.AssignmentId == assignmentId && x.Status == "Accepted")
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.CreatedAt)
        .Take(limit)
        .ToListAsync(ct);
    var metadata = await LoadAssignmentMetadataAsync(new[] { assignmentId }, cfg, httpFactory, ct);
    var users = await LoadUserSummariesAsync(rows.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value), cfg, httpFactory, ct);
    return Results.Ok(rows.Select(x => ToTopSolutionDto(x, canViewCode || x.UserId == uid.Value, metadata.GetValueOrDefault(x.AssignmentId), x.UserId.HasValue ? users.GetValueOrDefault(x.UserId.Value) : null)).ToList());
});

app.MapGet("/api/me/solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, Guid? assignmentId, int? days, int skip = 0, int take = 50, CancellationToken ct = default) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();

    var q = db.Submissions.AsNoTracking().Where(x => x.UserId == uid.Value);
    if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
    if (days.HasValue && days.Value > 0)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
        q = q.Where(x => x.CreatedAt >= since);
    }

    var rows = await q
        .OrderByDescending(x => x.CreatedAt)
        .Skip(Math.Max(0, skip))
        .Take(Math.Clamp(take, 1, 200))
        .ToListAsync(ct);
    var includeHiddenDetails = IsEditor(http, cfg);
    var metadata = await LoadAssignmentMetadataAsync(rows.Select(x => x.AssignmentId), cfg, httpFactory, ct);
    return Results.Ok(rows.Select(x => ToDto(x, includeHiddenDetails, metadata.GetValueOrDefault(x.AssignmentId))).ToList());
});
app.MapGet("/api/me/solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();

    var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    if (s == null) return Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
    if (s.UserId != uid.Value) return Results.Json(new { message = "Нет доступа к этому решению.", code = "SOLUTION_FORBIDDEN" }, statusCode: 403);
    var metadata = await LoadAssignmentMetadataAsync(new[] { s.AssignmentId }, cfg, httpFactory, ct);
    return Results.Ok(ToDto(s, includeSensitiveResult: IsEditor(http, cfg), metadata: metadata.GetValueOrDefault(s.AssignmentId)));
});
app.MapGet("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    if (s == null) return Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
    var metadata = await LoadAssignmentMetadataAsync(new[] { s.AssignmentId }, cfg, httpFactory, ct);
    return Results.Ok(ToDto(s, includeSensitiveResult: true, metadata: metadata.GetValueOrDefault(s.AssignmentId)));
});
app.MapDelete("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => { var s = await db.Submissions.FindAsync(id); if (s == null) return Results.NotFound(); db.Submissions.Remove(s); await db.SaveChangesAsync(); return Results.Ok(new { deleted = id }); });
app.MapGet("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, Guid? assignmentId, int? days, int skip = 0, int take = 50, CancellationToken ct = default) =>
{
    var q = db.Submissions.AsNoTracking().Where(x => x.UserId == userId);
    if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
    if (days.HasValue && days.Value > 0)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
        q = q.Where(x => x.CreatedAt >= since);
    }

    var rows = await q
        .OrderByDescending(x => x.CreatedAt)
        .Skip(Math.Max(0, skip))
        .Take(Math.Clamp(take, 1, 200))
        .ToListAsync(ct);
    var metadata = await LoadAssignmentMetadataAsync(rows.Select(x => x.AssignmentId), cfg, httpFactory, ct);
    return Results.Ok(rows.Select(x => ToDto(x, includeSensitiveResult: true, metadata: metadata.GetValueOrDefault(x.AssignmentId))).ToList());
});
app.MapDelete("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db, Guid? assignmentId, int? days) =>
{
    var q = db.Submissions.Where(x => x.UserId == userId);
    if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
    if (days.HasValue && days.Value > 0)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
        q = q.Where(x => x.CreatedAt >= since);
    }
    var rows = await q.ToListAsync();
    db.Submissions.RemoveRange(rows);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = rows.Count, assignmentId, days });
});
app.MapGet("/api/admin/solution-users", async (SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? q, int take = 200, CancellationToken ct = default) =>
{
    var ratingRows = await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).Take(1000).ToListAsync(ct);
    var ids = ratingRows.Select(x => x.UserId)
        .Concat(await db.Submissions.AsNoTracking().Where(x => x.UserId.HasValue).Select(x => x.UserId.Value).Distinct().Take(1000).ToListAsync(ct))
        .Distinct()
        .Take(1000)
        .ToArray();
    var users = await LoadUserSummariesAsync(ids, cfg, httpFactory, ct);
    var ratings = ratingRows.ToDictionary(x => x.UserId);
    var search = NormalizeSearch(q);
    var rows = ids
        .Select(id => new { Id = id, User = users.GetValueOrDefault(id), Rating = ratings.GetValueOrDefault(id) })
        .Where(x => string.IsNullOrWhiteSpace(search) || UserSummarySearchScore(x.User, x.Id, search) <= Math.Max(1, Math.Min(4, search.Length / 3)) || UserSummaryHaystack(x.User, x.Id).Contains(search, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(x => x.Rating?.TotalScore ?? 0)
        .ThenBy(x => UserLabel(x.User))
        .Take(Math.Clamp(take, 1, 500))
        .Select(x => new
        {
            id = x.Id,
            userId = x.Id,
            email = x.User?.Email ?? x.User?.MaskedEmail,
            maskedEmail = x.User?.MaskedEmail,
            displayName = UserLabel(x.User),
            fullName = UserLabel(x.User),
            firstName = x.User?.FirstName,
            lastName = x.User?.LastName,
            score = x.Rating?.TotalScore ?? 0,
            solved = x.Rating?.SolvedCount ?? 0
        })
        .ToList();
    return Results.Ok(rows);
});

app.MapGet("/api/leaderboard", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, Guid? courseId, int? days, Guid? groupId, string? q, int top = 100, int? page = null, int? pageSize = null, CancellationToken ct = default) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();

    Guid[]? groupUserIds = null;
    if (groupId.HasValue)
    {
        groupUserIds = await LoadGroupMemberIdsAsync(groupId.Value, cfg, httpFactory, ct);
        if (!IsEditor(http, cfg) && !groupUserIds.Contains(uid.Value))
        {
            return Results.Json(new { message = "Нет доступа к рейтингу этой группы.", code = "GROUP_FORBIDDEN" }, statusCode: 403);
        }
    }

    var since = days.HasValue && days.Value > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : (DateTimeOffset?)null;
    var codeRows = await db.Submissions.AsNoTracking()
        .Where(x => x.UserId.HasValue && x.Status == "Accepted")
        .Where(x => !since.HasValue || x.CreatedAt >= since.Value)
        .ToListAsync(ct);
    var imageRows = await db.ImageSolutions.AsNoTracking()
        .Where(x => x.Passed)
        .Where(x => !since.HasValue || x.CreatedAt >= since.Value)
        .ToListAsync(ct);

    if (groupUserIds is { Length: > 0 })
    {
        var members = groupUserIds.ToHashSet();
        codeRows = codeRows.Where(x => x.UserId.HasValue && members.Contains(x.UserId.Value)).ToList();
        imageRows = imageRows.Where(x => members.Contains(x.UserId)).ToList();
    }
    else if (groupId.HasValue)
    {
        codeRows = new List<SolutionSubmission>();
        imageRows = new List<UserImageTaskSolution>();
    }

    var assignmentIds = codeRows.Select(x => x.AssignmentId).Concat(imageRows.Select(x => x.AssignmentId)).Distinct().ToArray();
    var metadata = await LoadAssignmentMetadataAsync(assignmentIds, cfg, httpFactory, ct);

    var activityRows = new List<LeaderboardActivityRow>();
    activityRows.AddRange(codeRows.Where(x => x.UserId.HasValue).Select(x => new LeaderboardActivityRow(x.UserId.Value, x.AssignmentId, MetadataRating(metadata, x.AssignmentId), x.CreatedAt, "code")));
    activityRows.AddRange(imageRows.Select(x => new LeaderboardActivityRow(x.UserId, x.AssignmentId, MetadataRating(metadata, x.AssignmentId), x.CreatedAt, "image")));
    if (!groupId.HasValue || groupUserIds is { Length: > 0 })
    {
        activityRows.AddRange(await LoadTaskLeaderboardRowsAsync(courseId, days, groupUserIds, cfg, httpFactory, ct));
    }

    if (courseId.HasValue)
    {
        activityRows = activityRows.Where(x => metadata.TryGetValue(x.AssignmentId, out var m) ? m.CourseId == courseId.Value : true).ToList();
    }

    if (activityRows.Count == 0)
    {
        var requestedEmptyPagedShape = page.HasValue || pageSize.HasValue;
        if (requestedEmptyPagedShape) return Results.Ok(new PagedResult<object>(Array.Empty<object>(), Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 50), 0, false));
        return Results.Ok(Array.Empty<object>());
    }

    var aggregated = activityRows
        .GroupBy(x => x.UserId)
        .Select(g =>
        {
            var distinct = g.GroupBy(x => x.AssignmentId).Select(a => new { Rating = a.Max(z => z.Rating), Last = a.Max(z => z.SubmittedAt) }).ToList();
            return new
            {
                UserId = g.Key,
                SolvedAssignments = distinct.Count,
                Score = distinct.Sum(x => x.Rating),
                TotalAttempts = g.Count(),
                LastSubmitAt = distinct.Max(x => x.Last)
            };
        })
        .Where(x => x.SolvedAssignments > 0)
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.SolvedAssignments)
        .ThenByDescending(x => x.LastSubmitAt)
        .ToList();

    var users = await LoadUserSummariesAsync(aggregated.Select(x => x.UserId), cfg, httpFactory, ct);
    var badges = await LoadBadgeMapAsync(db, aggregated.Select(x => x.UserId), ct);
    var search = NormalizeSearch(q);

    var filtered = aggregated
        .Select(x => new { Row = x, User = users.GetValueOrDefault(x.UserId) })
        .Where(x => x.User == null || x.User.ShowInLeaderboard)
        .Where(x => string.IsNullOrWhiteSpace(search) || UserSummarySearchScore(x.User, x.Row.UserId, search) <= Math.Max(1, Math.Min(4, search.Length / 3)) || UserSummaryHaystack(x.User, x.Row.UserId).Contains(search, StringComparison.OrdinalIgnoreCase))
        .ToList();

    var requestedPagedShape = page.HasValue || pageSize.HasValue;
    var currentPage = Math.Max(1, page ?? 1);
    var size = requestedPagedShape ? Math.Clamp(pageSize ?? 20, 1, 50) : Math.Clamp(top, 1, 200);
    var offset = requestedPagedShape ? (currentPage - 1) * size : 0;
    var total = filtered.Count;

    var visible = filtered
        .Skip(offset)
        .Take(size)
        .Select((x, i) => new
        {
            rank = offset + i + 1,
            userId = x.Row.UserId,
            userName = UserLabel(x.User),
            displayName = UserLabel(x.User),
            email = x.User?.MaskedEmail,
            maskedEmail = x.User?.MaskedEmail,
            firstName = x.User?.FirstName,
            lastName = x.User?.LastName,
            avatarUrl = x.User?.AvatarUrl,
            location = x.User?.Location,
            education = x.User?.Education,
            totalScore = x.Row.Score,
            score = x.Row.Score,
            solved = x.Row.SolvedAssignments,
            solvedCount = x.Row.SolvedAssignments,
            solvedAssignments = x.Row.SolvedAssignments,
            totalAttempts = x.Row.TotalAttempts,
            lastSubmitAt = x.Row.LastSubmitAt,
            badges = badges.GetValueOrDefault(x.Row.UserId) ?? new List<object>()
        })
        .Cast<object>()
        .ToList();

    if (requestedPagedShape)
    {
        return Results.Ok(new PagedResult<object>(visible, currentPage, size, total, offset + visible.Count < total));
    }

    return Results.Ok(visible);
});
app.MapGet("/api/admin/leaderboard", async (SolutionsDbContext db) => Results.Ok(await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).ToListAsync()));

app.MapGet("/api/internal/users/{userId:guid}/activity-summary", async (Guid userId, SolutionsDbContext db, CancellationToken ct) =>
{
    var codeAttempts = await db.Submissions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
    var imageAttempts = await db.ImageSolutions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
    var solved = codeAttempts.Where(x => x.Status == "Accepted").Select(x => x.AssignmentId)
        .Concat(imageAttempts.Where(x => x.Passed).Select(x => x.AssignmentId))
        .Distinct()
        .Count();
    return Results.Ok(new
    {
        solvedAssignments = solved,
        totalAttempts = codeAttempts.Count + imageAttempts.Count,
        codeSolutions = codeAttempts.Count,
        imageSolutions = imageAttempts.Count,
        testAttempts = 0,
        mathAttempts = 0
    });
});

app.MapGet("/api/me/quotas", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();
    var status = await GetQuotaStatus(db, uid.Value);
    return Results.Ok(status);
});
app.MapGet("/api/quotas", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();
    return Results.Ok(await GetQuotaStatus(db, uid.Value));
});

app.MapGet("/api/badges", async (SolutionsDbContext db) =>
{
    var rows = await db.Badges.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    return Results.Ok(rows.Select(x => BadgeDto(x)).ToList());
});
app.MapPost("/api/badges", async (HttpRequest request, SolutionsDbContext db, CancellationToken ct) =>
{
    IFormCollection form;
    try { form = await request.ReadFormAsync(ct); }
    catch (Exception ex) { return Problem(400, "BADGE_FORM_READ_FAILED", "badges.form", "Не удалось прочитать форму создания бейджа.", ex.Message); }
    var name = form.TryGetValue("name", out var n) ? n.ToString().Trim() : string.Empty;
    var description = form.TryGetValue("description", out var d) ? d.ToString().Trim() : null;
    var file = form.Files.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(name)) return Problem(400, "BADGE_NAME_REQUIRED", "badges.validation", "Введите название бейджа.");
    if (file == null || file.Length == 0) return Problem(400, "BADGE_FILE_REQUIRED", "badges.validation", "Выберите SVG-файл бейджа.");
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext != ".svg" && file.ContentType != "image/svg+xml") return Problem(400, "BADGE_SVG_REQUIRED", "badges.validation", "Разрешены только SVG-файлы бейджей.");
    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(ms.ToArray());
    var badge = new Badge { Name = name, Description = string.IsNullOrWhiteSpace(description) ? null : description, ImageUrl = dataUri };
    db.Badges.Add(badge);
    await db.SaveChangesAsync(ct);
    return Results.Ok(BadgeDto(badge));
}).DisableAntiforgery();
app.MapDelete("/api/badges/{badgeId:guid}", async (Guid badgeId, SolutionsDbContext db) =>
{
    var badge = await db.Badges.FindAsync(badgeId);
    if (badge == null) return Results.NoContent();
    db.UserBadges.RemoveRange(await db.UserBadges.Where(x => x.BadgeId == badgeId).ToListAsync());
    db.Badges.Remove(badge);
    await db.SaveChangesAsync();
    return Results.NoContent();
});
app.MapPost("/api/badges/award", async (BadgeUserRequest req, SolutionsDbContext db) =>
{
    if (!await db.Badges.AnyAsync(x => x.Id == req.BadgeId)) return Results.NotFound(new { message = "Бейдж не найден.", code = "BADGE_NOT_FOUND" });
    if (!await db.UserBadges.AnyAsync(x => x.UserId == req.UserId && x.BadgeId == req.BadgeId)) db.UserBadges.Add(new UserBadge { UserId = req.UserId, BadgeId = req.BadgeId });
    await db.SaveChangesAsync();
    return Results.Ok(new { awarded = true });
});
app.MapPost("/api/badges/revoke", async (BadgeUserRequest req, SolutionsDbContext db) =>
{
    var rows = await db.UserBadges.Where(x => x.UserId == req.UserId && x.BadgeId == req.BadgeId).ToListAsync();
    db.UserBadges.RemoveRange(rows);
    await db.SaveChangesAsync();
    return Results.Ok(new { revoked = true });
});
app.MapGet("/api/badges/user/{userId:guid}", async (Guid userId, SolutionsDbContext db) =>
{
    var badgeIds = await db.UserBadges.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.AwardedAt).Select(x => x.BadgeId).ToListAsync();
    var rows = await db.Badges.AsNoTracking().Where(x => badgeIds.Contains(x.Id)).ToListAsync();
    return Results.Ok(rows.Select(x => BadgeDto(x)).ToList());
});

app.MapGet("/api/me/image-solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();
    var q = db.ImageSolutions.AsNoTracking().Where(x => x.UserId == uid.Value);
    if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
    if (days.HasValue && days.Value > 0)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
        q = q.Where(x => x.CreatedAt >= since);
    }
    var rows = await q.OrderByDescending(x => x.CreatedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync();
    return Results.Ok(rows.Select(x => ImageDto(x)).ToList());
});
app.MapGet("/api/me/image-solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();
    var row = await db.ImageSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (row == null) return Results.NotFound(new { message = "Решение не найдено.", code = "IMAGE_SOLUTION_NOT_FOUND" });
    if (row.UserId != uid.Value) return Results.Json(new { message = "Нет доступа к этому решению.", code = "IMAGE_SOLUTION_FORBIDDEN" }, statusCode: 403);
    return Results.Ok(ImageDto(row));
});
app.MapGet("/api/admin/image-solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => (await db.ImageSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)) is { } row ? Results.Ok(ImageDto(row)) : Results.NotFound(new { message = "Решение не найдено.", code = "IMAGE_SOLUTION_NOT_FOUND" }));
app.MapGet("/api/admin/users/{userId:guid}/image-solutions", async (Guid userId, SolutionsDbContext db, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
{
    var q = db.ImageSolutions.AsNoTracking().Where(x => x.UserId == userId);
    if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
    if (days.HasValue && days.Value > 0)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
        q = q.Where(x => x.CreatedAt >= since);
    }
    var rows = await q.OrderByDescending(x => x.CreatedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync();
    return Results.Ok(rows.Select(x => ImageDto(x)).ToList());
});
app.MapPost("/api/internal/image-solutions", async (InternalImageSolutionRequest request, SolutionsDbContext db, CancellationToken ct) =>
{
    if (request.UserId == Guid.Empty || request.AssignmentId == Guid.Empty)
    {
        return Problem(400, "IMAGE_SOLUTION_INVALID_REQUEST", "image-solutions.validation", "Не хватает userId или assignmentId для сохранения image-решения.");
    }

    var row = new UserImageTaskSolution
    {
        UserId = request.UserId,
        AssignmentId = request.AssignmentId,
        Language = NormalizeLanguage(request.Language) ?? "text",
        Code = request.Code ?? string.Empty,
        SimilarityPercent = Math.Clamp(request.SimilarityPercent, 0, 100),
        Passed = request.Passed,
        ResultJson = request.Result.HasValue
            ? request.Result.Value.GetRawText()
            : JsonSerializer.Serialize(new { passed = request.Passed, similarityPercent = request.SimilarityPercent }, JsonOptions())
    };
    db.ImageSolutions.Add(row);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ImageDto(row));
});
app.MapDelete("/api/admin/image-solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => { var row = await db.ImageSolutions.FindAsync(id); if (row == null) return Results.NotFound(); db.ImageSolutions.Remove(row); await db.SaveChangesAsync(); return Results.NoContent(); });

app.Run();

static async Task<JudgeSpec?> LoadJudgeSpecAsync(Guid assignmentId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var baseUrl = ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080");
    var client = httpFactory.CreateClient();
    using var msg = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/internal/assignments/{assignmentId}/judge-spec");
    AddInternalKey(msg, cfg);

    try
    {
        using var resp = await client.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JudgeSpec>(JsonOptions(), ct);
    }
    catch
    {
        return null;
    }
}

static async Task<EnqueueResult> EnqueueExecutionJobAsync(Guid submissionId, Guid assignmentId, Guid userId, string language, string code, string? input, JsonElement[] tests, JudgeSpec? spec, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var baseUrl = ServiceUrl(cfg, "ExecutionApi", "http://execution-api:8080");
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("Judge:EnqueueTimeoutSeconds", 10), 2, 60));

    var payload = new CreateExecutionJobRequest(
        submissionId,
        assignmentId,
        userId,
        language,
        code,
        input,
        tests,
        null,
        null,
        null,
        spec?.CodeForbiddenCalls,
        spec?.CodeRequiredCalls);

    using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/execution/jobs")
    {
        Content = JsonContent.Create(payload, options: JsonOptions())
    };
    AddInternalKey(msg, cfg);

    try
    {
        using var resp = await client.SendAsync(msg, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new EnqueueResult(false, null, $"Execution API не принял задачу проверки: {(int)resp.StatusCode}.", CloneJson(text));
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        var root = doc.RootElement.Clone();
        var jobId = TryReadGuid(root, "id") ?? (root.TryGetProperty("job", out var job) ? TryReadGuid(job, "id") : null);
        return new EnqueueResult(true, jobId, "Задача проверки создана.", root);
    }
    catch (Exception ex)
    {
        return new EnqueueResult(false, null, "Execution pipeline временно недоступен.", CloneJson(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions())));
    }
}

static async Task<SolutionSubmission?> WaitForTerminalSubmissionAsync(SolutionsDbContext db, Guid submissionId, IConfiguration cfg, CancellationToken ct)
{
    var timeoutMs = Math.Clamp(cfg.GetValue("Judge:SubmitWaitMilliseconds", 18000), 0, 60000);
    if (timeoutMs <= 0) return null;

    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
    {
        var row = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == submissionId, ct);
        if (row == null) return null;
        if (IsTerminalVerdict(row.Status))
        {
            return row;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
    }

    return await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == submissionId, ct);
}

static void ApplyLocalVerdict(SolutionSubmission sub, JudgeRunResult judge)
{
    sub.Status = judge.Verdict;
    sub.Score = judge.Score;
    sub.ResultJson = JsonSerializer.Serialize(new
    {
        verdict = judge.Verdict,
        status = judge.Verdict,
        pending = false,
        message = judge.Message,
        passedAllTests = judge.PassedAllTests,
        compileError = judge.CompileError,
        results = judge.Results,
        cases = judge.Results,
        raw = judge.Raw
    }, JsonOptions());
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
static string[] EffectiveAllowedLanguages(JudgeSpec spec)
{
    var values = spec.AllowedLanguages ?? [];
    var list = values.Select(NormalizeLanguage).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (list.Length > 0) return list;
    var fallback = NormalizeLanguage(spec.Language) ?? "csharp";
    return [fallback];
}
static bool IsAllowedLanguage(string language, JudgeSpec spec)
    => EffectiveAllowedLanguages(spec).Contains(language, StringComparer.OrdinalIgnoreCase);

static JsonElement[] ExtractTests(JudgeSpec? spec)
{
    // Hidden/autograder tests must come only from the task owner service.
    // Client-supplied tests are intentionally ignored here so a user cannot
    // submit a trivial test set and receive Accepted/100 for arbitrary code.
    if (spec?.TestCases is JsonElement tc)
    {
        var arr = ElementToArray(tc);
        if (arr.Length > 0) return arr;
    }

    if (spec?.Tests is JsonElement tests)
    {
        var arr = ElementToArray(tests);
        if (arr.Length > 0) return arr;
    }

    if (!string.IsNullOrWhiteSpace(spec?.TestsJson))
    {
        try
        {
            using var doc = JsonDocument.Parse(spec.TestsJson);
            return ElementToArray(doc.RootElement);
        }
        catch { }
    }

    return [];
}

static JsonElement[] ElementToArray(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Select(x => x.Clone()).ToArray();
    if (element.ValueKind == JsonValueKind.Object)
    {
        var merged = new List<JsonElement>();
        if (element.TryGetProperty("publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array)
            merged.AddRange(publicTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: false)));
        if (element.TryGetProperty("hiddenTests", out var hiddenTests) && hiddenTests.ValueKind == JsonValueKind.Array)
            merged.AddRange(hiddenTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: true)));
        if (merged.Count > 0) return merged.ToArray();

        foreach (var name in new[] { "testCases", "tests", "cases" })
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop.EnumerateArray().Select(x => x.Clone()).ToArray();
            }
        }
    }
    return [];
}

static JsonElement NormalizeTestCase(JsonElement item, bool hidden)
{
    if (item.ValueKind != JsonValueKind.Object) return item.Clone();
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        input = ReadString(item, "input") ?? ReadString(item, "stdin") ?? string.Empty,
        expectedOutput = ReadString(item, "expectedOutput") ?? ReadString(item, "expected") ?? ReadString(item, "stdout") ?? string.Empty,
        isHidden = hidden || ReadBool(item, "isHidden") || ReadBool(item, "hidden")
    }, JsonOptions()));
    return doc.RootElement.Clone();
}

static string? ReadString(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) ? v.ToString() : null;
static bool ReadBool(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

static JsonElement? ExtractResults(JsonElement root)
{
    if (root.ValueKind != JsonValueKind.Object) return null;
    foreach (var name in new[] { "results", "testCases", "cases" })
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            return prop.Clone();
        }
    }
    return null;
}

static bool IsPassedResult(JsonElement item)
{
    if (item.ValueKind != JsonValueKind.Object) return false;
    if (item.TryGetProperty("passed", out var passed) && passed.ValueKind is JsonValueKind.True or JsonValueKind.False) return passed.GetBoolean();
    if (item.TryGetProperty("status", out var status))
    {
        var value = NormalizeStatusKey(status.ToString());
        return value is "accepted" or "passed" or "success";
    }
    return false;
}

static bool IsCompileErrorResult(JsonElement item)
{
    if (item.ValueKind != JsonValueKind.Object) return false;
    if (item.TryGetProperty("status", out var status) && string.Equals(status.ToString(), "compile_error", StringComparison.OrdinalIgnoreCase)) return true;
    if (item.TryGetProperty("compileStderr", out var compileStderr) && !string.IsNullOrWhiteSpace(compileStderr.ToString())) return true;
    return false;
}

static bool IsCompileErrorRoot(JsonElement root)
{
    if (root.ValueKind != JsonValueKind.Object) return false;
    if (root.TryGetProperty("status", out var status))
    {
        var value = status.ToString();
        if (string.Equals(value, "compile_error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "compilation_error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "compileerror", StringComparison.OrdinalIgnoreCase)) return true;
    }
    if (root.TryGetProperty("compileStderr", out var compileStderr) && !string.IsNullOrWhiteSpace(compileStderr.ToString())) return true;
    if (root.TryGetProperty("stderr", out var stderr) && root.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number && exitCode.GetInt32() != 0 && !string.IsNullOrWhiteSpace(stderr.ToString())) return true;
    return false;
}

static JsonElement? CloneJson(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;
    try
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
    catch
    {
        return null;
    }
}

static Guid? TryReadGuid(JsonElement element, string propertyName)
{
    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop)) return null;
    return Guid.TryParse(prop.ToString(), out var id) ? id : null;
}

static string ServiceUrl(IConfiguration cfg, string name, string fallback)
{
    return (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');
}

static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
{
    var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
    if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
}

static async Task<Dictionary<Guid, AssignmentMetadata>> LoadAssignmentMetadataAsync(IEnumerable<Guid> assignmentIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var ids = assignmentIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
    if (ids.Length == 0) return new Dictionary<Guid, AssignmentMetadata>();

    var assignments = await PostInternalAsync<List<AssignmentSummaryDto>>(httpFactory, cfg, ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080"), "/api/internal/assignments/summaries", new AssignmentIdsRequest(ids), ct) ?? new List<AssignmentSummaryDto>();
    var courseIds = assignments.Select(x => x.CourseId).Where(x => x != Guid.Empty).Distinct().ToArray();
    var courses = await PostInternalAsync<List<CourseSummaryDto>>(httpFactory, cfg, ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), "/api/internal/courses/metadata", new CourseIdsRequest(courseIds), ct) ?? new List<CourseSummaryDto>();
    var courseMap = courses.ToDictionary(x => x.CourseId != Guid.Empty ? x.CourseId : x.Id, x => x.Title ?? x.CourseTitle ?? "Курс");

    var map = new Dictionary<Guid, AssignmentMetadata>();
    foreach (var a in assignments)
    {
        var id = a.AssignmentId != Guid.Empty ? a.AssignmentId : a.Id;
        if (id == Guid.Empty) continue;
        courseMap.TryGetValue(a.CourseId, out var courseTitle);
        map[id] = new AssignmentMetadata(id, a.CourseId, a.Title ?? a.AssignmentTitle ?? "Задание без названия", courseTitle ?? "Курс", Math.Max(0, a.Rating));
    }
    return map;
}

static int MetadataRating(Dictionary<Guid, AssignmentMetadata> metadata, Guid assignmentId)
    => metadata.TryGetValue(assignmentId, out var m) && m.Rating > 0 ? m.Rating : 1;

static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
    if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
    TaskForgeDebugTrace.UserSummaryRequest("solutions-api", "identity-api", ids);
    var rows = await PostInternalAsync<List<UserSummaryDto>>(httpFactory, cfg, ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080"), "/api/internal/users/summaries", new UserIdsRequest(ids), ct) ?? new List<UserSummaryDto>();
    var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
    TaskForgeDebugTrace.UserSummaryResponse("solutions-api", "identity-api", ids, map);
    return map;
}

static async Task<Guid[]> LoadGroupMemberIdsAsync(Guid groupId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var response = await GetInternalAsync<GroupMembersResponse>(httpFactory, cfg, ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/groups/{groupId}/members", ct);
    return response?.UserIds?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>();
}

static async Task<List<LeaderboardActivityRow>> LoadTaskLeaderboardRowsAsync(Guid? courseId, int? days, Guid[]? userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var rows = await PostInternalAsync<List<TaskActivityRowDto>>(httpFactory, cfg, ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080"), "/api/internal/activity/leaderboard", new ActivityLeaderboardRequest(courseId, days, userIds), ct) ?? new List<TaskActivityRowDto>();
    return rows.Where(x => x.UserId != Guid.Empty && x.AssignmentId != Guid.Empty)
        .Select(x => new LeaderboardActivityRow(x.UserId, x.AssignmentId, Math.Max(1, x.Rating), x.SubmittedAt, x.Kind ?? "task"))
        .ToList();
}

static async Task<Dictionary<Guid, List<object>>> LoadBadgeMapAsync(SolutionsDbContext db, IEnumerable<Guid> userIds, CancellationToken ct)
{
    var ids = userIds.Where(x => x != Guid.Empty).Distinct().ToArray();
    if (ids.Length == 0) return new Dictionary<Guid, List<object>>();
    var userBadges = await db.UserBadges.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync(ct);
    var badgeIds = userBadges.Select(x => x.BadgeId).Distinct().ToArray();
    var badges = await db.Badges.AsNoTracking().Where(x => badgeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
    return userBadges
        .Where(x => badges.ContainsKey(x.BadgeId))
        .GroupBy(x => x.UserId)
        .ToDictionary(g => g.Key, g => g.Select(x => badges[x.BadgeId]).Select(b => (object)new { b.Id, b.Name, b.Description, b.ImageUrl }).ToList());
}

static async Task<T?> PostInternalAsync<T>(IHttpClientFactory httpFactory, IConfiguration cfg, string baseUrl, string path, object payload, CancellationToken ct)
{
    try
    {
        var client = httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + path)
        {
            Content = JsonContent.Create(payload, options: JsonOptions())
        };
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

static string NormalizeStatusKey(string? value) => string.Join(string.Empty, (value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
static string NormalizeSearch(string? value) => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
static string UserLabel(UserSummaryDto? user)
{
    var name = (user?.DisplayName ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(name)) return name;
    var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    if (!string.IsNullOrWhiteSpace(full)) return full;
    var email = (user?.Email ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(email)) return email;
    var masked = (user?.MaskedEmail ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(masked)) return masked;
    return "Пользователь";
}
static string UserSummaryHaystack(UserSummaryDto? user, Guid id) => NormalizeSearch($"{id} {user?.Email} {user?.MaskedEmail} {user?.DisplayName} {user?.FirstName} {user?.LastName}");
static int UserSummarySearchScore(UserSummaryDto? user, Guid id, string query)
{
    var values = new[] { id.ToString(), user?.Email, user?.MaskedEmail, user?.DisplayName, user?.FirstName, user?.LastName }
        .Select(NormalizeSearch)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToArray();
    if (values.Any(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))) return 0;
    var best = int.MaxValue;
    foreach (var value in values)
    {
        best = Math.Min(best, Levenshtein(value, query));
        foreach (var token in value.Split(new[] { ' ', '@', '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) best = Math.Min(best, Levenshtein(token, query));
    }
    return best == int.MaxValue ? 999 : best;
}
static int Levenshtein(string a, string b)
{
    if (a == b) return 0;
    if (a.Length == 0) return b.Length;
    if (b.Length == 0) return a.Length;
    var prev = new int[b.Length + 1];
    var cur = new int[b.Length + 1];
    for (var j = 0; j <= b.Length; j++) prev[j] = j;
    for (var i = 1; i <= a.Length; i++)
    {
        cur[0] = i;
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
        }
        (prev, cur) = (cur, prev);
    }
    return prev[b.Length];
}

static bool IsTerminalVerdict(string? value) => value is not null && (string.Equals(value, "Accepted", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Rejected", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "CompileError", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "PolicyFailed", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "NoTestsConfigured", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "JudgeUnavailable", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "LanguageNotAllowed", StringComparison.OrdinalIgnoreCase));
static bool IsPendingVerdict(string? value) => value is not null && (string.Equals(value, "Preparing", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Queued", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Running", StringComparison.OrdinalIgnoreCase));
static string CleanVerdict(string? value) => string.IsNullOrWhiteSpace(value) ? "Rejected" : value.Trim();

static async Task AddRating(SolutionsDbContext db, Guid userId, int score, bool accepted)
{
    var rating = await db.UserRatings.FirstOrDefaultAsync(x => x.UserId == userId);
    if (rating == null)
    {
        rating = new UserRating { UserId = userId };
        db.UserRatings.Add(rating);
    }
    rating.AttemptsCount++;
    if (accepted)
    {
        rating.AcceptedCount++;
        rating.SolvedCount++;
        rating.TotalScore += score;
        rating.LastAcceptedAt = DateTimeOffset.UtcNow;
    }
    else rating.RejectedCount++;
    rating.UpdatedAt = DateTimeOffset.UtcNow;
}
static async Task<object> GetQuotaStatus(SolutionsDbContext db, Guid userId)
{
    var tasks = await StatusFor(db, userId, "tasks", 10, TimeSpan.FromSeconds(90));
    var top = await StatusFor(db, userId, "top", 5, TimeSpan.FromMinutes(30));
    return new { enabled = true, tasks, top, buckets = new[] { tasks, top }, remaining = tasks.remaining, capacity = tasks.capacity };
}
static async Task<QuotaView> StatusFor(SolutionsDbContext db, Guid userId, string bucket, int capacity, TimeSpan interval)
{
    var now = DateTimeOffset.UtcNow;
    var row = await db.UserQuotaBuckets.FirstOrDefaultAsync(x => x.UserId == userId && x.BucketType == bucket);
    if (row == null)
    {
        row = new UserQuotaBucket { UserId = userId, BucketType = bucket, Tokens = capacity, LastRefillAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.UserQuotaBuckets.Add(row);
        await db.SaveChangesAsync();
    }
    var elapsed = now - row.LastRefillAtUtc;
    if (elapsed.TotalSeconds >= interval.TotalSeconds)
    {
        var refill = (int)Math.Floor(elapsed.TotalSeconds / interval.TotalSeconds);
        row.Tokens = Math.Min(capacity, row.Tokens + refill);
        row.LastRefillAtUtc = row.LastRefillAtUtc.AddSeconds(refill * interval.TotalSeconds);
        row.UpdatedAtUtc = now;
        await db.SaveChangesAsync();
    }
    var next = row.LastRefillAtUtc.Add(interval);
    return new QuotaView(bucket, Math.Max(0, row.Tokens), capacity, row.Tokens >= capacity ? 0 : Math.Max(1, (int)Math.Ceiling((next - now).TotalSeconds)), next, row.Tokens > 0);
}
static Guid? CurrentUserId(HttpContext http, IConfiguration cfg) => TaskForgeRequestSecurity.UserId(http, cfg);
static bool IsEditor(HttpContext http, IConfiguration cfg)
{
    var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
    return principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor");
}
static IResult Unauthorized() => Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);
static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);
static object ToDto(SolutionSubmission x, bool includeSensitiveResult = false, AssignmentMetadata? metadata = null)
{
    var result = SanitizeSolutionResult(ParseJsonElement(x.ResultJson), includeSensitiveResult);
    var counts = CountCases(result);
    var acceptedStatus = string.Equals(x.Status, "Accepted", StringComparison.OrdinalIgnoreCase);
    var accepted = acceptedStatus && (counts.total == 0 || counts.failed == 0);
    return new
    {
        x.Id,
        x.AssignmentId,
        assignmentTitle = metadata?.Title,
        title = metadata?.Title,
        courseId = metadata?.CourseId,
        courseTitle = metadata?.CourseTitle,
        x.UserId,
        x.Language,
        x.Code,
        submittedCode = x.Code,
        verdict = accepted ? "Accepted" : x.Status,
        status = accepted ? "Accepted" : x.Status,
        x.Score,
        result = result.HasValue ? (object)result.Value : null,
        isPending = IsPendingVerdict(x.Status),
        passedAllTests = accepted,
        passedAll = accepted,
        compileError = string.Equals(x.Status, "CompileError", StringComparison.OrdinalIgnoreCase),
        policyFailed = string.Equals(x.Status, "PolicyFailed", StringComparison.OrdinalIgnoreCase),
        passedCount = counts.passed,
        failedCount = counts.failed,
        totalCount = counts.total,
        x.CreatedAt,
        createdAtUtc = x.CreatedAt,
        submittedAt = x.CreatedAt
    };
}
static object ToTopSolutionDto(SolutionSubmission x, bool includeCode, AssignmentMetadata? metadata = null, UserSummaryDto? user = null)
{
    var result = SanitizeSolutionResult(ParseJsonElement(x.ResultJson), includeHiddenDetails: false);
    var counts = CountCases(result);
    return new
    {
        x.Id,
        x.AssignmentId,
        assignmentTitle = metadata?.Title,
        title = metadata?.Title,
        courseId = metadata?.CourseId,
        courseTitle = metadata?.CourseTitle,
        x.UserId,
        userName = UserLabel(user),
        displayName = UserLabel(user),
        email = user?.MaskedEmail,
        maskedEmail = user?.MaskedEmail,
        x.Language,
        code = includeCode ? x.Code : null,
        submittedCode = includeCode ? x.Code : null,
        verdict = x.Status,
        status = x.Status,
        x.Score,
        result = result.HasValue ? (object)result.Value : null,
        passedCount = counts.passed,
        failedCount = counts.failed,
        totalCount = counts.total,
        codeHiddenUntilSolved = !includeCode,
        x.CreatedAt,
        createdAtUtc = x.CreatedAt,
        submittedAt = x.CreatedAt
    };
}
static JsonElement? SanitizeSolutionResult(JsonElement? result, bool includeHiddenDetails)
{
    if (includeHiddenDetails || !result.HasValue) return result;
    try
    {
        var node = JsonNode.Parse(result.Value.GetRawText());
        RemoveHiddenTestNodes(node);
        return JsonSerializer.SerializeToElement(node, JsonOptions());
    }
    catch
    {
        // Fail closed: if we cannot safely remove hidden tests, do not return
        // potentially sensitive result payload to a regular user.
        return null;
    }
}

static void RemoveHiddenTestNodes(JsonNode? node)
{
    if (node is JsonArray arr)
    {
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            if (IsHiddenTestNode(arr[i])) arr.RemoveAt(i);
            else RemoveHiddenTestNodes(arr[i]);
        }
        return;
    }

    if (node is not JsonObject obj) return;

    foreach (var child in obj.ToList())
    {
        if (IsHiddenTestNode(child.Value)) obj.Remove(child.Key);
        else RemoveHiddenTestNodes(child.Value);
    }
}

static bool IsHiddenTestNode(JsonNode? node)
    => node is JsonObject obj && (JsonBool(obj, "isHidden") || JsonBool(obj, "hidden"));

static bool JsonBool(JsonObject obj, string name)
    => obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var b) && b;

static object ToSubmitDto(SolutionSubmission x, bool includeSensitiveResult = false) => ToDto(x, includeSensitiveResult);
static object BadgeDto(Badge x) => new { x.Id, x.Name, x.Description, x.ImageUrl, x.CreatedAt };
static object ImageDto(UserImageTaskSolution x)
{
    var result = ParseJsonElement(x.ResultJson);
    var similarity = TryReadNumber(result, "similarityPercent") ?? TryReadNumber(result, "similarity") ?? x.SimilarityPercent;
    var threshold = TryReadNumber(result, "thresholdPercent") ?? TryReadNumber(result, "threshold");
    return new
    {
        x.Id,
        x.UserId,
        x.AssignmentId,
        x.Language,
        x.Code,
        submittedCode = x.Code,
        similarityPercent = similarity,
        thresholdPercent = threshold,
        x.Passed,
        result = result.HasValue ? (object)result.Value : null,
        referenceUrl = TryReadString(result, "referenceUrl") ?? TryReadString(result, "expectedUrl"),
        submittedUrl = TryReadString(result, "submittedUrl") ?? TryReadString(result, "actualUrl") ?? TryReadString(result, "renderedUrl"),
        stdout = TryReadString(result, "stdout"),
        stderr = TryReadString(result, "stderr"),
        runnerError = TryReadString(result, "runnerError") ?? TryReadString(result, "error"),
        x.CreatedAt,
        createdAtUtc = x.CreatedAt,
        submittedAt = x.CreatedAt
    };
}
static object? ParseJson(string? json) => ParseJsonElement(json);
static JsonElement? ParseJsonElement(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); } catch { return null; } }
static string? TryReadString(JsonElement? element, string name)
{
    if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
    var e = element.Value;
    if (!e.TryGetProperty(name, out var prop)) return null;
    var value = prop.ToString();
    return string.IsNullOrWhiteSpace(value) ? null : value;
}
static double? TryReadNumber(JsonElement? element, string name)
{
    if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
    var e = element.Value;
    if (!e.TryGetProperty(name, out var prop)) return null;
    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var number)) return number;
    if (double.TryParse(prop.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
    return null;
}
static (int passed, int failed, int total) CountCases(JsonElement? element)
{
    if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return (0, 0, 0);
    var root = element.Value;
    JsonElement cases;
    if (root.TryGetProperty("cases", out cases) || root.TryGetProperty("results", out cases) || root.TryGetProperty("testCases", out cases))
    {
        if (cases.ValueKind == JsonValueKind.Array)
        {
            var passed = 0;
            var failed = 0;
            foreach (var item in cases.EnumerateArray())
            {
                if (IsPassedResult(item)) passed++;
                else failed++;
            }
            return (passed, failed, passed + failed);
        }
    }
    return (0, 0, 0);
}
static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };
public sealed record AssignmentMetadata(Guid AssignmentId, Guid CourseId, string Title, string CourseTitle, int Rating);
public sealed record LeaderboardActivityRow(Guid UserId, Guid AssignmentId, int Rating, DateTimeOffset SubmittedAt, string Kind);
public sealed record AssignmentIdsRequest(Guid[]? AssignmentIds);
public sealed record CourseIdsRequest(Guid[]? CourseIds);
public sealed record UserIdsRequest(Guid[]? UserIds);
public sealed record ActivityLeaderboardRequest(Guid? CourseId, int? Days, Guid[]? UserIds);
public sealed class AssignmentSummaryDto
{
    public Guid Id { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid CourseId { get; set; }
    public string? Title { get; set; }
    public string? AssignmentTitle { get; set; }
    public int Rating { get; set; } = 1;
}
public sealed class CourseSummaryDto
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public string? Title { get; set; }
    public string? CourseTitle { get; set; }
}
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total, bool HasMore);

public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Email { get; set; }
    public string? MaskedEmail { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Location { get; set; }
    public string? Education { get; set; }
    public bool ShowInLeaderboard { get; set; } = true;
    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Email ?? MaskedEmail;
    }
}
public sealed class GroupMembersResponse
{
    public Guid GroupId { get; set; }
    public Guid[]? UserIds { get; set; }
}
public sealed class TaskActivityRowDto
{
    public Guid UserId { get; set; }
    public Guid AssignmentId { get; set; }
    public int Rating { get; set; } = 1;
    public DateTimeOffset SubmittedAt { get; set; }
    public string? Kind { get; set; }
}
public sealed record SubmitRequest(string? Language, string? Code, string? Input, JsonElement? Tests);
public sealed record SolutionVerdictRequest(string? Verdict, int Score, string? Message, JsonElement? Result);
public sealed record InternalImageSolutionRequest(Guid UserId, Guid AssignmentId, string? Language, string? Code, int SimilarityPercent, bool Passed, JsonElement? Result);
public sealed record JudgeSpec(Guid Id, string? Type, string? Language, string[]? AllowedLanguages, string[]? CodeForbiddenCalls, string[]? CodeRequiredCalls, JsonElement? Tests, JsonElement? TestCases, string? TestsJson);
public sealed record CreateExecutionJobRequest(Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, JsonElement[]? Tests, int? TimeLimitMs, int? MemoryLimitMb, string? TestsJson, string[]? CodeForbiddenCalls, string[]? CodeRequiredCalls);
public sealed record EnqueueResult(bool Created, Guid? JobId, string? Message, JsonElement? Raw);
public sealed record JudgeRunResult(string Verdict, int Score, bool PassedAllTests, bool CountInRating, string Message, JsonElement? Results, JsonElement? Raw, bool CompileError);
public sealed record BadgeUserRequest(Guid UserId, Guid BadgeId);

public sealed record QuotaView(string bucket, int remaining, int capacity, int retryAfterSeconds, DateTimeOffset nextRefillAtUtc, bool allowed);
