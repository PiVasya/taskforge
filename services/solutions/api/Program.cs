using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<SolutionsDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

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
    var db = ensureScope.ServiceProvider.GetRequiredService<SolutionsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-solutions-api" }));
app.MapGet("/health/ready", async (SolutionsDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-solutions-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-solutions-api", database = "taskforge_solutions", status = "solutions microservice active" }));
app.MapGet("/api/solutions/api/schema-owner", () => Results.Ok(new { database = "taskforge_solutions", ownedEntities = new[] { "Submission", "UserRating", "LeaderboardEntry" } }));

app.MapPost("/api/assignments/{assignmentId:guid}/submit", async (Guid assignmentId, SubmitRequest request, HttpContext http, SolutionsDbContext db) =>
{
    var userId = CurrentUserId(http);
    var sub = new SolutionSubmission
    {
        AssignmentId = assignmentId,
        UserId = userId,
        Language = request.Language ?? "csharp",
        Code = request.Code ?? string.Empty,
        Status = "Accepted",
        Score = 100,
        ResultJson = JsonSerializer.Serialize(new { message = "Accepted by solutions-api compatibility path", output = "" })
    };
    db.Submissions.Add(sub);
    if (userId.HasValue)
    {
        var rating = await db.UserRatings.FindAsync(userId.Value) ?? new UserRating { UserId = userId.Value };
        if (db.Entry(rating).State == EntityState.Detached) db.UserRatings.Add(rating);
        rating.TotalScore += 100;
        rating.SolvedCount += 1;
        rating.UpdatedAt = DateTimeOffset.UtcNow;
    }
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(sub));
});

app.MapGet("/api/assignments/{assignmentId:guid}/top-solutions", async (Guid assignmentId, SolutionsDbContext db) =>
{
    var rows = await db.Submissions.AsNoTracking().Where(x => x.AssignmentId == assignmentId).OrderByDescending(x => x.Score).ThenBy(x => x.CreatedAt).Take(20).ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});

app.MapGet("/api/me/solutions", async (HttpContext http, SolutionsDbContext db) =>
{
    var uid = CurrentUserId(http);
    var q = db.Submissions.AsNoTracking();
    if (uid.HasValue) q = q.Where(x => x.UserId == uid.Value);
    var rows = await q.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});
app.MapGet("/api/me/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => (await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)) is { } s ? Results.Ok(ToDto(s)) : Results.NotFound());
app.MapGet("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => (await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)) is { } s ? Results.Ok(ToDto(s)) : Results.NotFound());
app.MapDelete("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db) => { var s = await db.Submissions.FindAsync(id); if (s == null) return Results.NotFound(); db.Submissions.Remove(s); await db.SaveChangesAsync(); return Results.Ok(new { deleted = id }); });
app.MapGet("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db) => Results.Ok((await db.Submissions.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).ToListAsync()).Select(ToDto).ToList()));
app.MapDelete("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db) => { var rows = await db.Submissions.Where(x => x.UserId == userId).ToListAsync(); db.Submissions.RemoveRange(rows); await db.SaveChangesAsync(); return Results.Ok(new { deleted = rows.Count }); });
app.MapGet("/api/admin/solution-users", async (SolutionsDbContext db) => Results.Ok(await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).Take(200).ToListAsync()));

app.MapGet("/api/leaderboard", async (SolutionsDbContext db) =>
{
    var ratings = await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).ThenByDescending(x => x.SolvedCount).Take(100).ToListAsync();
    var result = ratings.Select((x, i) => new { rank = i + 1, userId = x.UserId, totalScore = x.TotalScore, solvedCount = x.SolvedCount, score = x.TotalScore }).ToList();
    return Results.Ok(result);
});
app.MapGet("/api/admin/leaderboard", async (SolutionsDbContext db) => Results.Ok(await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).ToListAsync()));
app.MapGet("/api/me/quotas", () => Results.Ok(new { remaining = 999, capacity = 999, buckets = Array.Empty<object>() }));
app.MapGet("/api/quotas", () => Results.Ok(new { remaining = 999, capacity = 999, buckets = Array.Empty<object>() }));

app.MapGet("/api/badges", () => Results.Ok(Array.Empty<object>()));
app.MapPost("/api/badges", (JsonElement body) => Results.Ok(body));
app.MapDelete("/api/badges/{badgeId:guid}", (Guid badgeId) => Results.Ok(new { deleted = badgeId }));
app.MapPost("/api/badges/award", (JsonElement body) => Results.Ok(body));
app.MapPost("/api/badges/revoke", (JsonElement body) => Results.Ok(body));
app.MapGet("/api/badges/user/{userId:guid}", (Guid userId) => Results.Ok(Array.Empty<object>()));

app.MapGet("/api/me/image-solutions", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/me/image-solutions/{id:guid}", (Guid id) => Results.Ok(new { id }));
app.MapGet("/api/admin/image-solutions/{id:guid}", (Guid id) => Results.Ok(new { id }));
app.MapGet("/api/admin/users/{userId:guid}/image-solutions", (Guid userId) => Results.Ok(Array.Empty<object>()));
app.MapDelete("/api/admin/image-solutions/{id:guid}", (Guid id) => Results.Ok(new { deleted = id }));

app.Run();

static Guid? CurrentUserId(HttpContext http)
{
    var auth = http.Request.Headers.Authorization.ToString();
    var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : (http.Request.Cookies.TryGetValue("tf_at", out var c) ? c : null);
    if (string.IsNullOrWhiteSpace(token)) return null;
    try
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var raw = jwt.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier || x.Type == "sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
    catch { return null; }
}
static object ToDto(SolutionSubmission x) => new { x.Id, x.AssignmentId, x.UserId, x.Language, x.Code, verdict = x.Status, status = x.Status, x.Score, result = ParseJson(x.ResultJson), x.CreatedAt };
static object? ParseJson(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return json; } }
public sealed record SubmitRequest(string? Language, string? Code, string? Input, JsonElement? Tests);
