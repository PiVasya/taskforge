using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<SolutionsDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

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
        return Results.Ok(ToSubmitDto(sub));
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
        return Results.Ok(ToSubmitDto(sub));
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
        return Results.Ok(ToSubmitDto(sub));
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
        return Results.Ok(ToSubmitDto(sub));
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
    return Results.Ok(ToSubmitDto(final ?? sub));
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
    return Results.Ok(ToDto(sub));
});

app.MapGet("/api/assignments/{assignmentId:guid}/top-solutions", async (Guid assignmentId, SolutionsDbContext db, int top = 20) =>
{
    var limit = Math.Clamp(top, 1, 100);
    var rows = await db.Submissions.AsNoTracking()
        .Where(x => x.AssignmentId == assignmentId && x.Status == "Accepted")
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.CreatedAt)
        .Take(limit)
        .ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});

app.MapGet("/api/me/solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
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
        .ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});
app.MapGet("/api/me/solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();

    var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (s == null) return Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
    if (s.UserId != uid.Value) return Results.Json(new { message = "Нет доступа к этому решению.", code = "SOLUTION_FORBIDDEN" }, statusCode: 403);
    return Results.Ok(ToDto(s));
});
app.MapGet("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => (await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)) is { } s ? Results.Ok(ToDto(s)) : Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" }));
app.MapDelete("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => { var s = await db.Submissions.FindAsync(id); if (s == null) return Results.NotFound(); db.Submissions.Remove(s); await db.SaveChangesAsync(); return Results.Ok(new { deleted = id }); });
app.MapGet("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
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
        .ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});
app.MapDelete("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db) => { var rows = await db.Submissions.Where(x => x.UserId == userId).ToListAsync(); db.Submissions.RemoveRange(rows); await db.SaveChangesAsync(); return Results.Ok(new { deleted = rows.Count }); });
app.MapGet("/api/admin/solution-users", async (SolutionsDbContext db, string? q, int take = 200) => Results.Ok(await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).Take(Math.Clamp(take, 1, 500)).Select(x => new { id = x.UserId, userId = x.UserId, email = x.UserId.ToString(), displayName = x.UserId.ToString(), score = x.TotalScore, solved = x.SolvedCount }).ToListAsync()));

app.MapGet("/api/leaderboard", async (SolutionsDbContext db) =>
{
    var ratings = await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).ThenByDescending(x => x.SolvedCount).Take(100).ToListAsync();
    return Results.Ok(ratings.Select((x, i) => new { rank = i + 1, userId = x.UserId, userName = x.UserId.ToString(), displayName = x.UserId.ToString(), totalScore = x.TotalScore, solvedCount = x.SolvedCount, score = x.TotalScore }).ToList());
});
app.MapGet("/api/admin/leaderboard", async (SolutionsDbContext db) => Results.Ok(await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).ToListAsync()));

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
        var value = status.ToString();
        return string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "OK", StringComparison.OrdinalIgnoreCase);
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
    var key = cfg["InternalApi:Key"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
    if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
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
static IResult Unauthorized() => Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);
static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);
static object ToDto(SolutionSubmission x)
{
    var result = ParseJsonElement(x.ResultJson);
    var counts = CountCases(result);
    var accepted = string.Equals(x.Status, "Accepted", StringComparison.OrdinalIgnoreCase);
    return new
    {
        x.Id,
        x.AssignmentId,
        x.UserId,
        x.Language,
        x.Code,
        submittedCode = x.Code,
        verdict = x.Status,
        status = x.Status,
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
static object ToSubmitDto(SolutionSubmission x) => ToDto(x);
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
public sealed record SubmitRequest(string? Language, string? Code, string? Input, JsonElement? Tests);
public sealed record SolutionVerdictRequest(string? Verdict, int Score, string? Message, JsonElement? Result);
public sealed record InternalImageSolutionRequest(Guid UserId, Guid AssignmentId, string? Language, string? Code, int SimilarityPercent, bool Passed, JsonElement? Result);
public sealed record JudgeSpec(Guid Id, string? Type, string? Language, string[]? AllowedLanguages, string[]? CodeForbiddenCalls, string[]? CodeRequiredCalls, JsonElement? Tests, JsonElement? TestCases, string? TestsJson);
public sealed record CreateExecutionJobRequest(Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, JsonElement[]? Tests, int? TimeLimitMs, int? MemoryLimitMb, string? TestsJson, string[]? CodeForbiddenCalls, string[]? CodeRequiredCalls);
public sealed record EnqueueResult(bool Created, Guid? JobId, string? Message, JsonElement? Raw);
public sealed record JudgeRunResult(string Verdict, int Score, bool PassedAllTests, bool CountInRating, string Message, JsonElement? Results, JsonElement? Raw, bool CompileError);
public sealed record BadgeUserRequest(Guid UserId, Guid BadgeId);

public sealed record QuotaView(string bucket, int remaining, int capacity, int retryAfterSeconds, DateTimeOffset nextRefillAtUtc, bool allowed);
