using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("support-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "support-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<SupportDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("support-api");
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<SupportDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for SupportDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for SupportDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<SupportDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("support");
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-support-api" }));
app.MapGet("/health/ready", async (SupportDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-support-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-support-api", database = "taskforge_support", status = "support microservice active" }));
app.MapGet("/api/support/schema-owner", () => Results.Ok(new { database = "taskforge_support", ownedEntities = new[] { "SupportTicket", "SupportMessage" } }));

app.MapGet("/api/support", async (HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
    if (!isAdmin && uid == null) return Unauthorized();

    var query = db.Tickets.AsNoTracking();
    if (!isAdmin) query = query.Where(x => x.UserId == uid);

    var tickets = await query.OrderByDescending(x => x.UpdatedAt).Take(500).ToListAsync(ct);
    var extras = await LoadTicketExtrasAsync(tickets.Select(x => x.Id), db, ct);
    var users = await LoadUserSummariesAsync(tickets.Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);

    return Results.Ok(tickets.Select(t => ToTicketDto(t, extras.GetValueOrDefault(t.Id), t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null)).ToList());
});

app.MapPost("/api/support", async (SupportRequest req, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, IHubContext<SupportHub> hub, CancellationToken ct) =>
{
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    if (uid == null) return Unauthorized();
    var subject = string.IsNullOrWhiteSpace(req.Subject) ? "Обращение" : req.Subject.Trim();
    var text = req.Message ?? req.Text ?? string.Empty;
    var now = DateTimeOffset.UtcNow;
    var t = new SupportTicket { UserId = uid, Subject = subject, Status = "open", CreatedAt = now, UpdatedAt = now };
    var m = new SupportMessage { TicketId = t.Id, UserId = uid, AuthorRole = "user", Text = text, CreatedAt = now };
    db.Tickets.Add(t);
    db.Messages.Add(m);
    await db.SaveChangesAsync(ct);
    var users = await LoadUserSummariesAsync(new[] { uid.Value }, cfg, httpFactory, ct);
    var ticketDto = ToTicketDto(t, new TicketExtra(1, Preview(text)), users.GetValueOrDefault(uid.Value));
    await hub.Clients.Group(SupportHubGroups.ForTicket(t.Id)).SendAsync("ReceiveMessage", t.Id.ToString(), ToMessageDto(m, users.GetValueOrDefault(uid.Value)), ct);
    return Results.Ok(new { id = t.Id, ticketId = t.Id, ticket = ticketDto, subject = t.Subject, status = t.Status, createdAt = t.CreatedAt, updatedAt = t.UpdatedAt });
});

app.MapGet("/api/support/{ticketId:guid}", async (Guid ticketId, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
    var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
    if (t == null) return Results.NotFound(new { message = "Обращение не найдено.", code = "SUPPORT_TICKET_NOT_FOUND" });
    if (!isAdmin && t.UserId != uid) return Results.Json(new { message = "Нет доступа к этому обращению.", code = "SUPPORT_TICKET_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

    var messages = await db.Messages.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
    var userIds = messages.Select(x => x.UserId).Concat(new[] { t.UserId }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
    var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);
    var extra = new TicketExtra(messages.Count, messages.Count == 0 ? null : Preview(messages[^1].Text));
    return Results.Ok(new
    {
        ticket = ToTicketDto(t, extra, t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null),
        messages = messages.Select(m => ToMessageDto(m, m.UserId.HasValue ? users.GetValueOrDefault(m.UserId.Value) : null)).ToList()
    });
});

app.MapPost("/api/support/{ticketId:guid}", async (Guid ticketId, SupportRequest req, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, IHubContext<SupportHub> hub, CancellationToken ct) =>
{
    var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
    var uid = TaskForgeRequestSecurity.UserId(http, cfg);
    var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
    var t = await db.Tickets.FindAsync([ticketId], ct);
    if (t == null) return Results.NotFound(new { message = "Обращение не найдено.", code = "SUPPORT_TICKET_NOT_FOUND" });
    if (!isAdmin && t.UserId != uid) return Results.Json(new { message = "Нет доступа к этому обращению.", code = "SUPPORT_TICKET_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

    var text = req.Message ?? req.Text ?? string.Empty;
    var now = DateTimeOffset.UtcNow;
    t.UpdatedAt = now;
    if (isAdmin && string.Equals(t.Status, "open", StringComparison.OrdinalIgnoreCase)) t.Status = "in-progress";
    var msg = new SupportMessage { TicketId = ticketId, UserId = uid, AuthorRole = isAdmin ? "admin" : "user", Text = text, CreatedAt = now };
    db.Messages.Add(msg);
    await db.SaveChangesAsync(ct);

    var users = uid.HasValue ? await LoadUserSummariesAsync(new[] { uid.Value }, cfg, httpFactory, ct) : new Dictionary<Guid, UserSummaryDto>();
    var dto = ToMessageDto(msg, uid.HasValue ? users.GetValueOrDefault(uid.Value) : null);
    await hub.Clients.Group(SupportHubGroups.ForTicket(ticketId)).SendAsync("ReceiveMessage", ticketId.ToString(), dto, ct);
    return Results.Ok(new { ticket = ToTicketDto(t, new TicketExtra(0, Preview(text)), (UserSummaryDto?)null), message = dto, id = t.Id, ticketId = t.Id, status = t.Status, updatedAt = t.UpdatedAt });
});

app.MapGet("/api/telegram/status", () => Results.Ok(new { configured = false }));
app.MapHub<SupportHub>("/hubs/support");
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
    TaskForgeDebugTrace.UserSummaryRequest("support-api", "identity-api", ids);
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
            TaskForgeDebugTrace.UserSummaryResponse("support-api", "identity-api", ids, empty);
            return empty;
        }
        var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
        var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
        TaskForgeDebugTrace.UserSummaryResponse("support-api", "identity-api", ids, map);
        return map;
    }
    catch
    {
        var empty = new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryResponse("support-api", "identity-api", ids, empty);
        return empty;
    }
}

static async Task<Dictionary<Guid, TicketExtra>> LoadTicketExtrasAsync(IEnumerable<Guid> ticketIds, SupportDbContext db, CancellationToken ct)
{
    var ids = ticketIds.Distinct().ToArray();
    if (ids.Length == 0) return new Dictionary<Guid, TicketExtra>();
    var messages = await db.Messages.AsNoTracking().Where(x => ids.Contains(x.TicketId)).OrderBy(x => x.CreatedAt).ToListAsync(ct);
    return messages.GroupBy(x => x.TicketId).ToDictionary(g => g.Key, g => new TicketExtra(g.Count(), Preview(g.Last().Text)));
}

static object ToTicketDto(SupportTicket x, TicketExtra? extra, UserSummaryDto? user) => new
{
    id = x.Id,
    ticketId = x.Id,
    subject = x.Subject,
    title = x.Subject,
    type = TicketType(x.Subject),
    status = x.Status,
    isClosed = IsClosed(x.Status),
    userId = x.UserId,
    user = user == null ? null : new
    {
        id = user.UserId,
        userId = user.UserId,
        login = user.Login,
        user.Email,
        user.MaskedEmail,
        user.FirstName,
        user.LastName,
        displayName = UserLabel(user),
        fullName = UserLabel(user)
    },
    messagesCount = extra?.MessagesCount ?? 0,
    lastMessagePreview = extra?.LastMessagePreview,
    createdAt = x.CreatedAt,
    updatedAt = x.UpdatedAt
};

static object ToMessageDto(SupportMessage x, UserSummaryDto? user)
{
    var isAdmin = string.Equals(x.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase);
    return new
    {
        id = x.Id,
        messageId = x.Id,
        ticketId = x.TicketId,
        userId = x.UserId,
        text = x.Text,
        body = x.Text,
        authorRole = x.AuthorRole,
        isFromAdmin = isAdmin,
        authorName = isAdmin ? "Поддержка" : UserLabel(user),
        createdAt = x.CreatedAt,
        createdAtUtc = x.CreatedAt
    };
}
static bool IsClosed(string? status) => string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase);
static string TicketType(string? subject)
{
    var s = (subject ?? string.Empty).ToLowerInvariant();
    if (s.Contains("ошиб") || s.Contains("bug") || s.Contains("баг")) return "Ошибка";
    if (s.Contains("иде") || s.Contains("feature") || s.Contains("предлож")) return "Идея";
    if (s.Contains("вопрос") || s.Contains("help")) return "Вопрос";
    return "Обращение";
}
static string? Preview(string? text)
{
    var value = (text ?? string.Empty).Trim();
    if (value.Length == 0) return null;
    value = value.Replace("\r", " ").Replace("\n", " ");
    return value.Length <= 160 ? value : value[..157] + "...";
}
static string UserLabel(UserSummaryDto? user)
{
    var name = (user?.DisplayName ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(name) && !LooksLikeEmail(name)) return name;
    var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    if (!string.IsNullOrWhiteSpace(full)) return full;
    var login = (user?.Login ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(login)) return login;
    var masked = (user?.MaskedEmail ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(masked)) return masked;
    return "Пользователь";
}
static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');

public sealed record SupportRequest(string? Subject, string? Message, string? Text);
public sealed record UserIdsRequest(Guid[] UserIds);
public sealed record TicketExtra(int MessagesCount, string? LastMessagePreview);
public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? MaskedEmail { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Login;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = MaskedEmail;
    }
}
public sealed class SupportHub : Hub
{
    private readonly SupportDbContext _db;
    private readonly IConfiguration _cfg;

    public SupportHub(SupportDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public async Task JoinTicket(string ticketId)
    {
        if (!Guid.TryParse(ticketId, out var id)) return;

        var http = Context.GetHttpContext();
        var uid = http == null ? null : TaskForgeRequestSecurity.UserId(http, _cfg);
        var isAdmin = Context.User?.Identity?.IsAuthenticated == true && TaskForgeRequestSecurity.HasAnyRole(Context.User, "Admin");
        if (!isAdmin && !uid.HasValue) return;

        var allowed = await _db.Tickets.AsNoTracking().AnyAsync(x => x.Id == id && (isAdmin || x.UserId == uid), Context.ConnectionAborted);
        if (!allowed) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, SupportHubGroups.ForTicket(id));
    }

    public Task LeaveTicket(string ticketId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, SupportHubGroups.FromString(ticketId));
}
public static class SupportHubGroups
{
    public static string ForTicket(Guid id) => $"support-ticket-{id:N}";
    public static string FromString(string? id) => Guid.TryParse(id, out var guid) ? ForTicket(guid) : $"support-ticket-{id}";
}
