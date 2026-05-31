using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<IdentityDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for IdentityDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for IdentityDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-identity-api" }));
app.MapGet("/health/ready", async (IdentityDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-identity-api" }) : Results.StatusCode(503);
});

app.MapGet("/", () => Results.Ok(new
{
    service = "taskforge-identity-api",
    database = "taskforge_identity",
    status = "identity microservice active",
    endpoints = new[] { "/api/auth/login", "/api/auth/register", "/api/profile", "/api/me/ui-settings" }
}));

app.MapGet("/api/identity/schema-owner", () => Results.Ok(new
{
    database = "taskforge_identity",
    ownedEntities = new[] { "User", "UserUiSettings", "UserLoginLog" }
}));

app.MapPost("/api/auth/register", async (RegisterRequest request, IdentityDbContext db) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest(new { message = "Email is required" });
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6) return Results.BadRequest(new { message = "Password must be at least 6 characters" });
    if (await db.Users.AnyAsync(x => x.Email == email)) return Results.BadRequest(new { message = "Пользователь с таким email уже существует." });

    var firstUser = !await db.Users.AnyAsync();
    var salt = NewSalt();
    var user = new IdentityUser
    {
        Email = email,
        FirstName = (request.FirstName ?? string.Empty).Trim(),
        LastName = (request.LastName ?? string.Empty).Trim(),
        PasswordSalt = salt,
        PasswordHash = HashPassword(request.Password, salt),
        Role = firstUser ? "Admin" : "User"
    };
    db.Users.Add(user);
    db.UiSettings.Add(new UserUiSettings { UserId = user.Id, DataJson = DefaultUiSettingsJson() });
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Пользователь зарегистрирован", userId = user.Id, role = user.Role });
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var email = NormalizeEmail(request.Email);
    var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
    if (user == null || !VerifyPassword(request.Password ?? string.Empty, user.PasswordSalt, user.PasswordHash))
    {
        return Unauthorized("Неверный e-mail или пароль. Проверьте данные или зарегистрируйтесь.", "INVALID_CREDENTIALS");
    }

    user.LastLoginAt = DateTimeOffset.UtcNow;
    db.LoginLogs.Add(new UserLoginLog
    {
        UserId = user.Id,
        IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        UserAgent = http.Request.Headers.UserAgent.ToString()
    });
    await db.SaveChangesAsync();

    var accessLifetime = TimeSpan.FromMinutes(cfg.GetValue<int?>("Jwt:ExpireMinutes") ?? 120);
    var refreshLifetime = TimeSpan.FromDays(7);
    var access = CreateJwt(user, cfg, accessLifetime, "access");
    var refresh = CreateJwt(user, cfg, refreshLifetime, "refresh");
    SetAuthCookies(http, access, refresh, accessLifetime, refreshLifetime);
    return Results.Ok(new { accessToken = access, user = ToProfile(user) });
});

app.MapPost("/api/auth/refresh", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var principal = ValidateToken(ReadCookie(http, "tf_rt"), cfg, validateLifetime: true);
    var uid = principal == null ? null : TryGetUserId(principal);
    if (uid == null) return Unauthorized("Сессия истекла. Войдите заново.");
    if (!string.Equals(principal!.FindFirstValue("token_type"), "refresh", StringComparison.OrdinalIgnoreCase)) return Unauthorized("Сессия истекла. Войдите заново.");

    var user = await db.Users.FindAsync(uid.Value);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");

    var accessLifetime = TimeSpan.FromMinutes(cfg.GetValue<int?>("Jwt:ExpireMinutes") ?? 120);
    var refreshLifetime = TimeSpan.FromDays(7);
    var access = CreateJwt(user, cfg, accessLifetime, "access");
    var refresh = CreateJwt(user, cfg, refreshLifetime, "refresh");
    SetAuthCookies(http, access, refresh, accessLifetime, refreshLifetime);
    return Results.Ok(new { accessToken = access, user = ToProfile(user) });
});

app.MapPost("/api/auth/logout", (HttpContext http) =>
{
    ClearAuthCookies(http);
    return Results.Ok(new { message = "ok" });
});

app.MapGet("/api/profile", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    return user == null ? Unauthorized("Сессия истекла. Войдите заново.") : Results.Ok(ToProfile(user));
});

app.MapPut("/api/profile", async (ProfileUpdateRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    user.FirstName = (request.FirstName ?? user.FirstName).Trim();
    user.LastName = (request.LastName ?? user.LastName).Trim();
    await db.SaveChangesAsync();
    return Results.Ok(ToProfile(user));
});

app.MapPost("/api/profile/change-password", async (ChangePasswordRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    if (!VerifyPassword(request.CurrentPassword ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Results.BadRequest(new { message = "Неверный текущий пароль" });
    var salt = NewSalt();
    user.PasswordSalt = salt;
    user.PasswordHash = HashPassword(request.NewPassword ?? string.Empty, salt);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Пароль изменён" });
});

app.MapPost("/api/profile/change-email", async (ChangeEmailRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    if (!VerifyPassword(request.Password ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Results.BadRequest(new { message = "Неверный пароль" });
    var email = NormalizeEmail(request.NewEmail);
    if (await db.Users.AnyAsync(x => x.Email == email && x.Id != user.Id)) return Results.BadRequest(new { message = "Email уже занят" });
    user.Email = email;
    await db.SaveChangesAsync();
    return Results.Ok(ToProfile(user));
});

app.MapGet("/api/me/ui-settings", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    var row = await db.UiSettings.FindAsync(user.Id);
    if (row == null)
    {
        row = new UserUiSettings { UserId = user.Id, DataJson = DefaultUiSettingsJson() };
        db.UiSettings.Add(row);
        await db.SaveChangesAsync();
    }
    return Results.Text(row.DataJson, "application/json");
});

app.MapPut("/api/me/ui-settings", async (JsonElement payload, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    var row = await db.UiSettings.FindAsync(user.Id);
    if (row == null)
    {
        row = new UserUiSettings { UserId = user.Id };
        db.UiSettings.Add(row);
    }
    row.DataJson = payload.GetRawText();
    row.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Text(row.DataJson, "application/json");
});

app.MapGet("/api/users/{userId:guid}/public-profile", async (Guid userId, IdentityDbContext db) =>
{
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
    return user == null ? Results.NotFound() : Results.Ok(new
    {
        user.Id,
        user.Email,
        user.FirstName,
        user.LastName,
        displayName = DisplayName(user),
        user.Role,
        user.CreatedAt
    });
});


app.MapGet("/api/admin/solution-users", async (IdentityDbContext db, string? q, int take = 50) =>
{
    var query = db.Users.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(q))
    {
        var qq = q.Trim().ToLowerInvariant();
        query = query.Where(x => x.Email.ToLower().Contains(qq) || x.FirstName.ToLower().Contains(qq) || x.LastName.ToLower().Contains(qq));
    }
    var list = await query.OrderBy(x => x.Email).Take(Math.Clamp(take, 1, 200)).Select(x => new
    {
        x.Id,
        x.Email,
        x.FirstName,
        x.LastName,
        displayName = (x.FirstName + " " + x.LastName).Trim(),
        x.Role
    }).ToListAsync();
    return Results.Ok(list);
});

app.MapGet("/api/admin/users", async (IdentityDbContext db, string? q, int take = 50) =>
{
    var query = db.Users.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(q))
    {
        var qq = q.Trim().ToLowerInvariant();
        query = query.Where(x => x.Email.ToLower().Contains(qq) || x.FirstName.ToLower().Contains(qq) || x.LastName.ToLower().Contains(qq));
    }
    var list = await query.OrderBy(x => x.Email).Take(Math.Clamp(take, 1, 200)).Select(x => new
    {
        x.Id,
        x.Email,
        x.FirstName,
        x.LastName,
        displayName = (x.FirstName + " " + x.LastName).Trim(),
        x.Role,
        x.CreatedAt,
        x.LastLoginAt,
        featureRoles = Array.Empty<string>(),
        groups = Array.Empty<object>(),
        codeSolutions = 0,
        testAttempts = 0,
        imageSolutions = 0,
        mathAttempts = 0
    }).ToListAsync();
    return Results.Ok(list);
});

app.MapPut("/api/admin/users/{userId:guid}", async (Guid userId, AdminUserUpdateRequest request, IdentityDbContext db) =>
{
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.NotFound();
    if (!string.IsNullOrWhiteSpace(request.Email)) user.Email = NormalizeEmail(request.Email);
    if (request.FirstName != null) user.FirstName = request.FirstName.Trim();
    if (request.LastName != null) user.LastName = request.LastName.Trim();
    if (!string.IsNullOrWhiteSpace(request.Role)) user.Role = request.Role.Trim();
    await db.SaveChangesAsync();
    return Results.Ok(ToProfile(user));
});

app.MapDelete("/api/admin/users/{userId:guid}", async (Guid userId, IdentityDbContext db) =>
{
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.NotFound();
    db.Users.Remove(user);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "deleted" });
});

app.MapGet("/api/admin/feature-roles", () => Results.Ok(new[]
{
    new { id = "admin", code = "Admin", title = "Admin", isActive = true },
    new { id = "editor", code = "Editor", title = "Editor", isActive = true }
}));
app.MapGet("/api/admin/feature-roles/users", () => Results.Ok(Array.Empty<object>()));
app.MapPost("/api/admin/feature-roles", (JsonElement body) => Results.Ok(body));
app.MapPut("/api/admin/feature-roles/{id}", (string id, JsonElement body) => Results.Ok(body));
app.MapDelete("/api/admin/feature-roles/{id}", (string id) => Results.Ok(new { message = "deleted", id }));
app.MapPost("/api/admin/feature-roles/users/{userId:guid}/roles", (Guid userId, JsonElement body) => Results.Ok(new { userId }));
app.MapDelete("/api/admin/feature-roles/users/{userId:guid}/roles/{code}", (Guid userId, string code) => Results.Ok(new { userId, code }));

app.MapGet("/api/integrations/telegram/status", () => Results.Ok(new { linked = false }));
app.MapPost("/api/integrations/telegram/code", () => Results.Ok(new { code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), expiresInSeconds = 600 }));
app.MapDelete("/api/integrations/telegram/unlink", () => Results.Ok(new { linked = false }));

app.Run();

static IResult Unauthorized(string message, string code = "UNAUTHORIZED") => Results.Json(new { message, code, severity = "warning" }, statusCode: StatusCodes.Status401Unauthorized);
static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
static string HashPassword(string password, string salt)
{
    using var sha = SHA256.Create();
    return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + password)));
}
static bool VerifyPassword(string password, string salt, string hash) => CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(HashPassword(password, salt)), Convert.FromBase64String(hash));
static string DisplayName(IdentityUser user) => string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim() is { Length: > 0 } s ? s : user.Email;
static object ToProfile(IdentityUser user) => new
{
    user.Id,
    user.Email,
    user.FirstName,
    user.LastName,
    displayName = DisplayName(user),
    user.Role,
    roles = new[] { user.Role },
    isAdmin = string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase),
    isEditor = string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase) || string.Equals(user.Role, "Editor", StringComparison.OrdinalIgnoreCase),
    user.CreatedAt,
    user.LastLoginAt
};
static string DefaultUiSettingsJson() => "{\"colorTheme\":\"pink\",\"mode\":\"dark\",\"bgFx\":false,\"fxMode\":\"random\",\"fxVariant\":\"2\",\"codeSolveLayout\":\"split\"}";
static string? ReadCookie(HttpContext http, string name) => http.Request.Cookies.TryGetValue(name, out var v) ? v : null;
static string? ReadBearer(HttpContext http)
{
    var auth = http.Request.Headers.Authorization.ToString();
    return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
}
static ClaimsPrincipal? ValidateToken(string? token, IConfiguration cfg, bool validateLifetime)
{
    if (string.IsNullOrWhiteSpace(token)) return null;
    try
    {
        var key = Encoding.UTF8.GetBytes(cfg["Jwt:Key"] ?? cfg["Jwt:SigningKey"] ?? "dev_change_me_please_change_me_please_32_chars");
        return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = validateLifetime,
            ValidIssuer = cfg["Jwt:Issuer"] ?? "TaskForge",
            ValidAudience = cfg["Jwt:Audience"] ?? "TaskForge",
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ClockSkew = TimeSpan.FromSeconds(30)
        }, out _);
    }
    catch { return null; }
}
static Guid? TryGetUserId(ClaimsPrincipal principal)
{
    var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
    return Guid.TryParse(raw, out var id) ? id : null;
}
static async Task<IdentityUser?> FindCurrentUserAsync(HttpContext http, IdentityDbContext db, IConfiguration cfg)
{
    var principal = ValidateToken(ReadBearer(http) ?? ReadCookie(http, "tf_at"), cfg, validateLifetime: true);
    var uid = principal == null ? null : TryGetUserId(principal);
    return uid == null ? null : await db.Users.FindAsync(uid.Value);
}
static string CreateJwt(IdentityUser user, IConfiguration cfg, TimeSpan lifetime, string tokenType)
{
    var key = Encoding.UTF8.GetBytes(cfg["Jwt:Key"] ?? cfg["Jwt:SigningKey"] ?? "dev_change_me_please_change_me_please_32_chars");
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Role, user.Role),
        new("role", user.Role),
        new("roles", user.Role),
        new("primary_role", user.Role),
        new("token_type", tokenType),
        new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };
    var creds = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(cfg["Jwt:Issuer"] ?? "TaskForge", cfg["Jwt:Audience"] ?? "TaskForge", claims, expires: DateTime.UtcNow.Add(lifetime), signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(token);
}
static void SetAuthCookies(HttpContext http, string access, string refresh, TimeSpan accessLifetime, TimeSpan refreshLifetime)
{
    var secure = string.Equals(http.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase) || http.Request.IsHttps;
    http.Response.Cookies.Append("tf_at", access, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.Add(accessLifetime), Path = "/" });
    http.Response.Cookies.Append("tf_rt", refresh, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.Add(refreshLifetime), Path = "/" });
}
static void ClearAuthCookies(HttpContext http)
{
    http.Response.Cookies.Delete("tf_at", new CookieOptions { Path = "/" });
    http.Response.Cookies.Delete("tf_rt", new CookieOptions { Path = "/" });
}

public sealed record RegisterRequest(string? Email, string? Password, string? FirstName, string? LastName);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record ProfileUpdateRequest(string? FirstName, string? LastName);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record ChangeEmailRequest(string? NewEmail, string? Password);
public sealed record AdminUserUpdateRequest(string? Email, string? FirstName, string? LastName, string? Role);
