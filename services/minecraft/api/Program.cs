using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("minecraft-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<MinecraftDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("minecraft-api");
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<MinecraftDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for MinecraftDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for MinecraftDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<MinecraftDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("minecraft");
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-minecraft-api" }));
app.MapGet("/health/ready", async (MinecraftDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-minecraft-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-minecraft-api", database = "taskforge_minecraft", status = "minecraft microservice active" }));
app.MapGet("/api/minecraft/schema-owner", () => Results.Ok(new { database = "taskforge_minecraft", ownedEntities = new[] { "MinecraftLink", "MinecraftChatMessage" } }));

app.MapGet("/api/integrations/minecraft/status", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
{
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    if (uid == null) return Unauthorized();
    var confirmed = await db.Links.AsNoTracking().Where(x => x.UserId == uid && x.Confirmed).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
    var count = await db.Links.AsNoTracking().CountAsync(x => x.UserId == uid, ct);
    return Results.Ok(new
    {
        linked = confirmed != null,
        playerName = confirmed?.PlayerName,
        nick = confirmed?.PlayerName,
        minecraftNick = confirmed?.PlayerName,
        playerUuid = confirmed?.PlayerUuid,
        uuid = confirmed?.PlayerUuid,
        minecraftUuid = confirmed?.PlayerUuid,
        linkedAtUtc = confirmed?.CreatedAt,
        linkCount = count
    });
});

app.MapPost("/api/integrations/minecraft/request", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
{
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    if (uid == null) return Unauthorized();
    var active = await db.Links.Where(x => x.UserId == uid && !x.Confirmed).ToListAsync(ct);
    if (active.Count > 0) db.Links.RemoveRange(active);
    var link = new MinecraftLink { UserId = uid, Code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow };
    db.Links.Add(link);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { code = link.Code, expiresInSeconds = 600 });
});

app.MapPost("/api/integrations/minecraft/confirm", async (MinecraftConfirmRequest req, HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
{
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    if (uid == null) return Unauthorized();
    var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
    var link = await db.Links.Where(x => x.UserId == uid && x.Code == code).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
    if (link == null) return Results.NotFound(new { message = "Код привязки не найден для текущего пользователя.", code = "MINECRAFT_LINK_CODE_NOT_FOUND" });
    link.Confirmed = true;
    link.PlayerName = string.IsNullOrWhiteSpace(req.PlayerName) ? link.PlayerName : req.PlayerName.Trim();
    link.PlayerUuid = string.IsNullOrWhiteSpace(req.PlayerUuid) ? link.PlayerUuid : req.PlayerUuid.Trim();
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { linked = true, playerName = link.PlayerName, nick = link.PlayerName, uuid = link.PlayerUuid, minecraftUuid = link.PlayerUuid });
});

app.MapDelete("/api/integrations/minecraft/unlink", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
{
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    if (uid == null) return Unauthorized();
    var links = await db.Links.Where(x => x.UserId == uid).ToListAsync(ct);
    db.Links.RemoveRange(links);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { linked = false });
});

app.MapGet("/api/admin/minecraft-links", async (MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? query, CancellationToken ct) =>
{
    var links = await db.Links.AsNoTracking().Where(x => x.Confirmed).OrderByDescending(x => x.CreatedAt).Take(1000).ToListAsync(ct);
    var users = await LoadUserSummariesAsync(links.Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);
    var grouped = links.GroupBy(x => x.UserId ?? Guid.Empty).Select(g =>
    {
        var latest = g.OrderByDescending(x => x.CreatedAt).First();
        var user = latest.UserId.HasValue ? users.GetValueOrDefault(latest.UserId.Value) : null;
        return new
        {
            id = latest.Id,
            userId = latest.UserId,
            fullName = UserLabel(user),
            displayName = UserLabel(user),
            email = user?.Email ?? user?.MaskedEmail,
            minecraftNick = latest.PlayerName,
            nick = latest.PlayerName,
            minecraftUuid = latest.PlayerUuid,
            uuid = latest.PlayerUuid,
            linkedAtUtc = latest.CreatedAt,
            linkCount = g.Count(),
            totalScore = 0,
            effectiveScore = 0,
            totalPenalty = 0,
            weeklyJoinEvents = 0,
            featureRoles = Array.Empty<string>(),
            lastPenaltyAtUtc = (DateTimeOffset?)null
        };
    }).ToList();

    var search = (query ?? string.Empty).Trim().ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(search))
    {
        grouped = grouped.Where(x => string.Join(' ', x.fullName, x.email, x.minecraftNick, x.minecraftUuid).ToLowerInvariant().Contains(search)).ToList();
    }
    return Results.Ok(grouped);
});

app.MapGet("/api/integrations/minecraft/chat/meta", () => Results.Ok(new { enabled = true, maxLength = 500 }));
app.MapGet("/api/integrations/minecraft/chat/messages", async (MinecraftDbContext db, CancellationToken ct) => Results.Ok(await db.ChatMessages.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).OrderBy(x => x.CreatedAt).ToListAsync(ct)));
app.MapPost("/api/integrations/minecraft/chat/messages", async (MinecraftChatRequest req, MinecraftDbContext db, CancellationToken ct) => { var msg = new MinecraftChatMessage { Author = string.IsNullOrWhiteSpace(req.Author) ? "web" : req.Author!, Text = req.Text ?? string.Empty }; db.ChatMessages.Add(msg); await db.SaveChangesAsync(ct); return Results.Ok(msg); });
app.MapHub<MinecraftChatHub>("/hubs/minecraft-chat");
app.Run();

static IResult Unauthorized() => Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);
static string ServiceUrl(IConfiguration cfg, string name, string fallback) => (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');
static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
{
    var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
    if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
}
static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };
static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(1000).ToArray();
    if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
    TaskForgeDebugTrace.UserSummaryRequest("minecraft-api", "identity-api", ids);
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
            TaskForgeDebugTrace.UserSummaryResponse("minecraft-api", "identity-api", ids, empty);
            return empty;
        }
        var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
        var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
        TaskForgeDebugTrace.UserSummaryResponse("minecraft-api", "identity-api", ids, map);
        return map;
    }
    catch
    {
        var empty = new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryResponse("minecraft-api", "identity-api", ids, empty);
        return empty;
    }
}
static string UserLabel(UserSummaryDto? user)
{
    var name = (user?.DisplayName ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(name)) return name;
    var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    if (!string.IsNullOrWhiteSpace(full)) return full;
    return string.IsNullOrWhiteSpace(user?.Email) ? "Пользователь" : user!.Email!.Trim();
}

public sealed record MinecraftConfirmRequest(string? Code, string? PlayerName, string? PlayerUuid);
public sealed record MinecraftChatRequest(string? Author, string? Text);
public sealed record UserIdsRequest(Guid[] UserIds);
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
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Email ?? MaskedEmail;
    }
}
public sealed class MinecraftChatHub : Hub { }
