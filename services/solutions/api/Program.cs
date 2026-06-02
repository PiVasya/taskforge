using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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

app.MapPost("/api/assignments/{assignmentId:guid}/submit", async (Guid assignmentId, SubmitRequest request, HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var userId = CurrentUserId(http, cfg);
    if (userId == null) return Unauthorized();
    var sub = new SolutionSubmission
    {
        AssignmentId = assignmentId,
        UserId = userId,
        Language = string.IsNullOrWhiteSpace(request.Language) ? "csharp" : request.Language.Trim(),
        Code = request.Code ?? string.Empty,
        Status = "Accepted",
        Score = 100,
        ResultJson = JsonSerializer.Serialize(new { verdict = "Accepted", message = "Решение принято. В микросервисной версии детальный judge pipeline будет обогащать результат после подключения execution events.", stdout = "", stderr = "" }, JsonOptions())
    };
    db.Submissions.Add(sub);
    await AddRating(db, userId.Value, 100, accepted: true);
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(sub));
});

app.MapGet("/api/assignments/{assignmentId:guid}/top-solutions", async (Guid assignmentId, SolutionsDbContext db) =>
{
    var rows = await db.Submissions.AsNoTracking().Where(x => x.AssignmentId == assignmentId && x.Status == "Accepted").OrderByDescending(x => x.Score).ThenBy(x => x.CreatedAt).Take(20).ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});

app.MapGet("/api/me/solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http, cfg);
    var q = db.Submissions.AsNoTracking();
    if (uid.HasValue) q = q.Where(x => x.UserId == uid.Value);
    var rows = await q.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});
app.MapGet("/api/me/solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http, cfg);
    var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (s == null) return Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
    if (uid.HasValue && s.UserId != uid.Value) return Results.Json(new { message = "Нет доступа к этому решению.", code = "SOLUTION_FORBIDDEN" }, statusCode: 403);
    return Results.Ok(ToDto(s));
});
app.MapGet("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => (await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)) is { } s ? Results.Ok(ToDto(s)) : Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" }));
app.MapDelete("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => { var s = await db.Submissions.FindAsync(id); if (s == null) return Results.NotFound(); db.Submissions.Remove(s); await db.SaveChangesAsync(); return Results.Ok(new { deleted = id }); });
app.MapGet("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db) => Results.Ok((await db.Submissions.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).ToListAsync()).Select(ToDto).ToList()));
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

app.MapGet("/api/me/image-solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, Guid? assignmentId, int skip = 0, int take = 50) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Unauthorized();
    var q = db.ImageSolutions.AsNoTracking().Where(x => x.UserId == uid.Value);
    if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
    var rows = await q.OrderByDescending(x => x.CreatedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync();
    return Results.Ok(rows.Select(x => ImageDto(x)).ToList());
});
app.MapGet("/api/me/image-solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http, cfg);
    var row = await db.ImageSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (row == null) return Results.NotFound(new { message = "Решение не найдено.", code = "IMAGE_SOLUTION_NOT_FOUND" });
    if (uid.HasValue && row.UserId != uid.Value) return Results.Json(new { message = "Нет доступа к этому решению.", code = "IMAGE_SOLUTION_FORBIDDEN" }, statusCode: 403);
    return Results.Ok(ImageDto(row));
});
app.MapGet("/api/admin/image-solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => (await db.ImageSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)) is { } row ? Results.Ok(ImageDto(row)) : Results.NotFound(new { message = "Решение не найдено.", code = "IMAGE_SOLUTION_NOT_FOUND" }));
app.MapGet("/api/admin/users/{userId:guid}/image-solutions", async (Guid userId, SolutionsDbContext db) =>
{
    var rows = await db.ImageSolutions.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).ToListAsync();
    return Results.Ok(rows.Select(x => ImageDto(x)).ToList());
});
app.MapDelete("/api/admin/image-solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => { var row = await db.ImageSolutions.FindAsync(id); if (row == null) return Results.NotFound(); db.ImageSolutions.Remove(row); await db.SaveChangesAsync(); return Results.NoContent(); });

app.Run();

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
static object ToDto(SolutionSubmission x) => new { x.Id, x.AssignmentId, x.UserId, x.Language, x.Code, verdict = x.Status, status = x.Status, x.Score, result = ParseJson(x.ResultJson), x.CreatedAt, submittedAt = x.CreatedAt };
static object BadgeDto(Badge x) => new { x.Id, x.Name, x.Description, x.ImageUrl, x.CreatedAt };
static object ImageDto(UserImageTaskSolution x) => new { x.Id, x.UserId, x.AssignmentId, x.Language, x.Code, x.SimilarityPercent, x.Passed, result = ParseJson(x.ResultJson), x.CreatedAt, submittedAt = x.CreatedAt };
static object? ParseJson(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return json; } }
static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };
public sealed record SubmitRequest(string? Language, string? Code, string? Input, JsonElement? Tests);
public sealed record BadgeUserRequest(Guid UserId, Guid BadgeId);

public sealed record QuotaView(string bucket, int remaining, int capacity, int retryAfterSeconds, DateTimeOffset nextRefillAtUtc, bool allowed);
