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
    await SeedFeatureRoles(db);
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

app.UseTaskForgeRequestSecurity("identity");

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

app.MapPost("/api/auth/register", async (RegisterRequest request, IdentityDbContext db, IConfiguration cfg) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest(new { message = "Email is required" });
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6) return Results.BadRequest(new { message = "Password must be at least 6 characters" });
    if (await db.Users.AnyAsync(x => x.Email == email)) return Results.BadRequest(new { message = "Пользователь с таким email уже существует." });

    var firstUser = !await db.Users.AnyAsync();
    var role = ResolveInitialRole(email, firstUser, cfg);
    var salt = NewSalt();
    var user = new IdentityUser
    {
        Email = email,
        FirstName = (request.FirstName ?? string.Empty).Trim(),
        LastName = (request.LastName ?? string.Empty).Trim(),
        PasswordSalt = salt,
        PasswordHash = HashPassword(request.Password, salt),
        Role = role
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
    var roles = await RolesForUser(db, user);
    var access = CreateJwt(user, cfg, accessLifetime, "access", roles);
    var refresh = CreateJwt(user, cfg, refreshLifetime, "refresh", roles);
    SetAuthCookies(http, access, refresh, accessLifetime, refreshLifetime);
    return Results.Ok(new { accessToken = access, user = ToProfile(user, roles) });
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
    var roles = await RolesForUser(db, user);
    var access = CreateJwt(user, cfg, accessLifetime, "access", roles);
    var refresh = CreateJwt(user, cfg, refreshLifetime, "refresh", roles);
    SetAuthCookies(http, access, refresh, accessLifetime, refreshLifetime);
    return Results.Ok(new { accessToken = access, user = ToProfile(user, roles) });
});

app.MapPost("/api/auth/logout", (HttpContext http) =>
{
    ClearAuthCookies(http);
    return Results.Ok(new { message = "ok" });
});

app.MapGet("/api/profile", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    return user == null ? Unauthorized("Сессия истекла. Войдите заново.") : Results.Ok(ToProfile(user, await RolesForUser(db, user)));
});

app.MapPut("/api/profile", async (ProfileUpdateRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    user.FirstName = (request.FirstName ?? user.FirstName).Trim();
    user.LastName = (request.LastName ?? user.LastName).Trim();
    if (request.PhoneNumber != null) user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
    if (request.ProfilePictureUrl != null) user.ProfilePictureUrl = string.IsNullOrWhiteSpace(request.ProfilePictureUrl) ? null : request.ProfilePictureUrl.Trim();
    if (request.AdditionalDataJson != null) user.AdditionalDataJson = string.IsNullOrWhiteSpace(request.AdditionalDataJson) ? null : request.AdditionalDataJson;
    await db.SaveChangesAsync();
    return Results.Ok(ToProfile(user, await RolesForUser(db, user)));
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
    return Results.Ok(ToProfile(user, await RolesForUser(db, user)));
});

app.MapPost("/api/profile/reveal-email", async (RevealEmailRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    if (!VerifyPassword(request.Password ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Results.BadRequest(new { message = "Неверный пароль" });
    return Results.Ok(new { email = user.Email, revealedAt = DateTimeOffset.UtcNow });
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
    if (user == null) return Results.NotFound(new { message = "Профиль не найден" });

    var extra = ReadPublicProfileExtra(user.AdditionalDataJson);
    return Results.Ok(new
    {
        user.Id,
        user.FirstName,
        user.LastName,
        avatarUrl = user.ProfilePictureUrl,
        profilePictureUrl = user.ProfilePictureUrl,
        displayName = PublicDisplayName(user),
        user.CreatedAt,
        bio = extra.Bio,
        location = extra.Location,
        education = extra.Education,
        github = extra.Github,
        telegram = extra.Telegram,
        website = extra.Website,
        skills = extra.Skills,
        showInLeaderboard = extra.ShowInLeaderboard,
        solvedAssignments = 0,
        totalAttempts = 0
    });
});


app.MapGet("/api/admin/solution-users", async (IdentityDbContext db, string? q, int take = 50) =>
{
    var query = FilterUsers(db.Users.AsNoTracking(), q, null, false);
    var rows = await query.OrderBy(x => x.Email).Take(Math.Clamp(take, 1, 200)).ToListAsync();
    return Results.Ok(rows.Select(u => ToAdminUserDto(u)).ToList());
});

app.MapGet("/api/admin/users", async (
    IdentityDbContext db,
    string? query,
    string? q,
    string? role,
    bool linkedOnly = false,
    string? sortBy = "createdAt",
    string? sortDir = "desc",
    int take = 300) =>
{
    var rowsQuery = FilterUsers(db.Users.AsNoTracking(), query ?? q, role, linkedOnly);
    rowsQuery = SortUsers(rowsQuery, sortBy, sortDir);
    var rows = await rowsQuery.Take(Math.Clamp(take, 1, 500)).ToListAsync();
    var total = await FilterUsers(db.Users.AsNoTracking(), query ?? q, role, linkedOnly).CountAsync();
    var linked = 0;
    var admins = rows.Count(x => string.Equals(x.Role, "Admin", StringComparison.OrdinalIgnoreCase));
    return Results.Ok(new
    {
        items = rows.Select(u => ToAdminUserDto(u)).ToList(),
        stats = new { total, linked, admins }
    });
});

app.MapPut("/api/admin/users/{userId:guid}", async (Guid userId, AdminUserUpdateRequest request, IdentityDbContext db) =>
{
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
    if (!string.IsNullOrWhiteSpace(request.Email)) user.Email = NormalizeEmail(request.Email);
    if (request.FirstName != null) user.FirstName = request.FirstName.Trim();
    if (request.LastName != null) user.LastName = request.LastName.Trim();
    if (request.PhoneNumber != null) user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
    if (request.ProfilePictureUrl != null) user.ProfilePictureUrl = string.IsNullOrWhiteSpace(request.ProfilePictureUrl) ? null : request.ProfilePictureUrl.Trim();
    if (!string.IsNullOrWhiteSpace(request.Role)) user.Role = NormalizeRole(request.Role);
    await db.SaveChangesAsync();
    return Results.Ok(ToAdminUserDto(user));
});

app.MapDelete("/api/admin/users/{userId:guid}", async (Guid userId, IdentityDbContext db) =>
{
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
    db.Users.Remove(user);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Пользователь удалён", deleted = true });
});

app.MapGet("/api/admin/feature-roles", async (IdentityDbContext db) => Results.Ok(await db.FeatureRoles.AsNoTracking().OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, title = x.Title, x.Description, x.IsActive }).ToListAsync()));
app.MapGet("/api/admin/feature-roles/users", async (IdentityDbContext db, string? query, int limit = 50) =>
{
    var rows = await FilterUsers(db.Users.AsNoTracking(), query, null, false).OrderBy(x => x.Email).Take(Math.Clamp(limit, 1, 200)).ToListAsync();
    var ids = rows.Select(x => x.Id).ToHashSet();
    var roleRows = await db.UserFeatureRoles.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync();
    return Results.Ok(rows.Select(u => ToAdminUserDto(u, roleRows.Where(r => r.UserId == u.Id).Select(r => r.Code).ToArray())).ToList());
});
app.MapPost("/api/admin/feature-roles", async (FeatureRoleRequest request, IdentityDbContext db) =>
{
    var code = NormalizeRoleCode(request.Code);
    if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { message = "Код роли обязателен.", code = "ROLE_CODE_REQUIRED" });
    if (await db.FeatureRoles.AnyAsync(x => x.Code == code)) return Results.Conflict(new { message = "Такая роль уже существует.", code = "ROLE_ALREADY_EXISTS" });
    var role = new FeatureRole { Code = code, Title = string.IsNullOrWhiteSpace(request.Title) ? code : request.Title.Trim(), Description = request.Description, IsActive = request.IsActive ?? true };
    db.FeatureRoles.Add(role);
    await db.SaveChangesAsync();
    return Results.Ok(new { role.Id, role.Code, title = role.Title, role.Description, role.IsActive });
});
app.MapPut("/api/admin/feature-roles/{id:guid}", async (Guid id, FeatureRoleRequest request, IdentityDbContext db) =>
{
    var role = await db.FeatureRoles.FindAsync(id);
    if (role == null) return Results.NotFound(new { message = "Роль не найдена.", code = "ROLE_NOT_FOUND" });
    var nextCode = NormalizeRoleCode(request.Code ?? role.Code);
    if (!string.Equals(role.Code, nextCode, StringComparison.OrdinalIgnoreCase) && await db.FeatureRoles.AnyAsync(x => x.Code == nextCode)) return Results.Conflict(new { message = "Такая роль уже существует.", code = "ROLE_ALREADY_EXISTS" });
    var oldCode = role.Code;
    role.Code = nextCode;
    role.Title = string.IsNullOrWhiteSpace(request.Title) ? role.Title : request.Title.Trim();
    role.Description = request.Description ?? role.Description;
    role.IsActive = request.IsActive ?? role.IsActive;
    role.UpdatedAt = DateTimeOffset.UtcNow;
    if (!string.Equals(oldCode, role.Code, StringComparison.OrdinalIgnoreCase))
    {
        foreach (var ur in await db.UserFeatureRoles.Where(x => x.Code == oldCode).ToListAsync()) ur.Code = role.Code;
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { role.Id, role.Code, title = role.Title, role.Description, role.IsActive });
});
app.MapDelete("/api/admin/feature-roles/{id:guid}", async (Guid id, IdentityDbContext db) =>
{
    var role = await db.FeatureRoles.FindAsync(id);
    if (role == null) return Results.NoContent();
    db.UserFeatureRoles.RemoveRange(await db.UserFeatureRoles.Where(x => x.Code == role.Code).ToListAsync());
    db.FeatureRoles.Remove(role);
    await db.SaveChangesAsync();
    return Results.NoContent();
});
app.MapPost("/api/admin/feature-roles/users/{userId:guid}/roles", async (Guid userId, RoleAssignRequest request, IdentityDbContext db) =>
{
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
    var code = NormalizeRoleCode(request.Code);
    if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { message = "Роль не указана.", code = "ROLE_REQUIRED" });
    if (!await db.FeatureRoles.AnyAsync(x => x.Code == code)) db.FeatureRoles.Add(new FeatureRole { Code = code, Title = code, IsActive = true });
    if (code is "Admin" or "Editor" or "LearningEditor" or "Minecraft") user.Role = code == "Admin" ? "Admin" : user.Role;
    if (!await db.UserFeatureRoles.AnyAsync(x => x.UserId == userId && x.Code == code)) db.UserFeatureRoles.Add(new UserFeatureRole { UserId = userId, Code = code });
    await db.SaveChangesAsync();
    return Results.Ok(ToAdminUserDto(user, await RolesForUser(db, user)));
});
app.MapDelete("/api/admin/feature-roles/users/{userId:guid}/roles/{code}", async (Guid userId, string code, IdentityDbContext db) =>
{
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
    var normalized = NormalizeRoleCode(code);
    db.UserFeatureRoles.RemoveRange(await db.UserFeatureRoles.Where(x => x.UserId == userId && x.Code == normalized).ToListAsync());
    if (string.Equals(user.Role, normalized, StringComparison.OrdinalIgnoreCase)) user.Role = "User";
    await db.SaveChangesAsync();
    return Results.Ok(ToAdminUserDto(user, await RolesForUser(db, user)));
});

app.MapGet("/api/integrations/telegram/status", () => Results.Ok(new { linked = false }));
app.MapPost("/api/integrations/telegram/code", () => Results.Ok(new { code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), expiresInSeconds = 600 }));
app.MapDelete("/api/integrations/telegram/unlink", () => Results.Ok(new { linked = false }));

app.Run();


static IQueryable<IdentityUser> FilterUsers(IQueryable<IdentityUser> query, string? text, string? role, bool linkedOnly)
{
    if (!string.IsNullOrWhiteSpace(text))
    {
        var q = text.Trim().ToLowerInvariant();
        query = query.Where(x => x.Email.ToLower().Contains(q) || x.FirstName.ToLower().Contains(q) || x.LastName.ToLower().Contains(q));
    }
    if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role == role.Trim());
    if (linkedOnly) query = query.Where(x => false);
    return query;
}
static IQueryable<IdentityUser> SortUsers(IQueryable<IdentityUser> query, string? sortBy, string? sortDir)
{
    var desc = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
    return (sortBy ?? "createdAt") switch
    {
        "email" => desc ? query.OrderByDescending(x => x.Email) : query.OrderBy(x => x.Email),
        "role" => desc ? query.OrderByDescending(x => x.Role) : query.OrderBy(x => x.Role),
        "fullName" => desc ? query.OrderByDescending(x => x.FirstName).ThenByDescending(x => x.LastName) : query.OrderBy(x => x.FirstName).ThenBy(x => x.LastName),
        "lastLoginAt" => desc ? query.OrderByDescending(x => x.LastLoginAt) : query.OrderBy(x => x.LastLoginAt),
        _ => desc ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt)
    };
}
static object ToAdminUserDto(IdentityUser user, IReadOnlyCollection<string>? featureRoles = null) => new
{
    user.Id,
    user.Email,
    maskedEmail = MaskEmail(user.Email),
    user.FirstName,
    user.LastName,
    user.PhoneNumber,
    user.ProfilePictureUrl,
    fullName = DisplayName(user),
    displayName = DisplayName(user),
    user.Role,
    roles = MergeRoles(user.Role, featureRoles),
    featureRoles = MergeRoles(user.Role, featureRoles),
    user.CreatedAt,
    user.LastLoginAt,
    telegramUsername = (string?)null,
    telegramLinkedAtUtc = (DateTimeOffset?)null,
    minecraftNick = (string?)null,
    minecraftLinkedAtUtc = (DateTimeOffset?)null,
    emailConfirmed = true,
    lockoutEnabled = false,
    codeSolutions = 0,
    passedTests = 0,
    imageSolutions = 0,
    mathSolutions = 0
};
static string NormalizeRole(string? role) => (role ?? "User").Trim() switch
{
    "Admin" => "Admin",
    "Editor" => "Editor",
    "LearningEditor" => "LearningEditor",
    "Minecraft" => "Minecraft",
    _ => "User"
};
static async Task SeedFeatureRoles(IdentityDbContext db)
{
    var defaults = new[]
    {
        new FeatureRole { Code = "Admin", Title = "Администратор", Description = "Полный доступ к админке", IsActive = true },
        new FeatureRole { Code = "Editor", Title = "Редактор", Description = "Редактирование заданий и курсов", IsActive = true },
        new FeatureRole { Code = "LearningEditor", Title = "Редактор ЦТ", Description = "Редактирование learning/quiz контента", IsActive = true },
        new FeatureRole { Code = "Minecraft", Title = "Minecraft", Description = "Доступ к Minecraft-инструментам", IsActive = true }
    };
    foreach (var role in defaults)
    {
        if (!await db.FeatureRoles.AnyAsync(x => x.Code == role.Code)) db.FeatureRoles.Add(role);
    }
    await db.SaveChangesAsync();
}
static async Task<string[]> RolesForUser(IdentityDbContext db, IdentityUser user)
{
    var rows = await db.UserFeatureRoles.AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.Code).ToListAsync();
    return MergeRoles(user.Role, rows);
}
static string[] MergeRoles(string? primaryRole, IEnumerable<string>? featureRoles)
{
    return new[] { NormalizeRole(primaryRole) }
        .Concat(featureRoles ?? Array.Empty<string>())
        .Select(NormalizeRoleCode)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
static string NormalizeRoleCode(string? role) => (role ?? string.Empty).Trim() switch
{
    "admin" or "Admin" => "Admin",
    "editor" or "Editor" => "Editor",
    "learning-editor" or "LearningEditor" => "LearningEditor",
    "minecraft" or "Minecraft" => "Minecraft",
    var x => string.IsNullOrWhiteSpace(x) ? string.Empty : x
};

static IResult Unauthorized(string message, string code = "UNAUTHORIZED") => Results.Json(new { message, code, severity = "warning" }, statusCode: StatusCodes.Status401Unauthorized);

static string ResolveInitialRole(string email, bool firstUser, IConfiguration cfg)
{
    var adminEmails = (cfg["Bootstrap:AdminEmails"] ?? string.Empty)
        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeEmail)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    if (adminEmails.Contains(email)) return "Admin";

    var firstUserIsAdmin = cfg.GetValue("Bootstrap:FirstUserIsAdmin", false);
    return firstUser && firstUserIsAdmin ? "Admin" : "User";
}
static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
static string HashPassword(string password, string salt)
{
    using var sha = SHA256.Create();
    return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + password)));
}
static bool VerifyPassword(string password, string salt, string hash) => CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(HashPassword(password, salt)), Convert.FromBase64String(hash));
static string MaskEmail(string? email)
{
    var value = (email ?? string.Empty).Trim();
    var at = value.IndexOf('@');
    if (at <= 0) return "Почта не указана";
    var name = value[..at];
    var domain = value[(at + 1)..];
    var dot = domain.LastIndexOf('.');
    var host = dot > 0 ? domain[..dot] : domain;
    var zone = dot > 0 ? domain[(dot + 1)..] : string.Empty;
    var maskedName = name.Length <= 2 ? $"{name[..1]}***" : $"{name[..Math.Min(2, name.Length)]}***";
    var maskedHost = host.Length <= 2 ? $"{host[..1]}***" : $"{host[..Math.Min(2, host.Length)]}***";
    return string.IsNullOrWhiteSpace(zone) ? $"{maskedName}@{maskedHost}" : $"{maskedName}@{maskedHost}.{zone}";
}
static string PublicDisplayName(IdentityUser user) => string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim() is { Length: > 0 } s ? s : "Пользователь TaskForge";
static string DisplayName(IdentityUser user) => string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim() is { Length: > 0 } s ? s : "Пользователь TaskForge";
static object ToProfile(IdentityUser user, IReadOnlyCollection<string>? featureRoles = null) => new
{
    user.Id,
    user.Email,
    maskedEmail = MaskEmail(user.Email),
    user.FirstName,
    user.LastName,
    user.PhoneNumber,
    user.ProfilePictureUrl,
    user.AdditionalDataJson,
    displayName = DisplayName(user),
    user.Role,
    roles = MergeRoles(user.Role, featureRoles),
    isAdmin = MergeRoles(user.Role, featureRoles).Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase)),
    isEditor = MergeRoles(user.Role, featureRoles).Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase) || string.Equals(r, "Editor", StringComparison.OrdinalIgnoreCase)),
    user.CreatedAt,
    user.LastLoginAt
};

static PublicProfileExtra ReadPublicProfileExtra(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return PublicProfileExtra.Empty;
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var links = root.TryGetProperty("links", out var linkObj) && linkObj.ValueKind == JsonValueKind.Object ? linkObj : default(JsonElement);
        var skills = new List<string>();
        if (root.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in skillsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) skills.Add(value.Trim());
                }
            }
        }

        var showInLeaderboard = true;
        if (root.TryGetProperty("showInLeaderboard", out var showEl) && (showEl.ValueKind == JsonValueKind.True || showEl.ValueKind == JsonValueKind.False))
        {
            showInLeaderboard = showEl.GetBoolean();
        }

        return new PublicProfileExtra(
            ReadJsonString(root, "bio"),
            ReadJsonString(root, "location"),
            ReadJsonString(root, "education"),
            ReadJsonString(links, "github"),
            ReadJsonString(links, "telegram"),
            ReadJsonString(links, "website"),
            skills,
            showInLeaderboard
        );
    }
    catch
    {
        return PublicProfileExtra.Empty;
    }
}

static string? ReadJsonString(JsonElement element, string propertyName)
{
    if (element.ValueKind != JsonValueKind.Object) return null;
    if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) return null;
    var s = value.GetString();
    return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
static string DefaultUiSettingsJson() => "{\"colorTheme\":\"pink\",\"mode\":\"dark\",\"bgFx\":false,\"fxMode\":\"random\",\"fxVariant\":\"2\",\"codeSolveLayout\":\"split\",\"showSidebarToggle\":true,\"sidebarCollapsed\":false}";
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
static string CreateJwt(IdentityUser user, IConfiguration cfg, TimeSpan lifetime, string tokenType, IReadOnlyCollection<string>? featureRoles = null)
{
    var key = Encoding.UTF8.GetBytes(cfg["Jwt:Key"] ?? cfg["Jwt:SigningKey"] ?? "dev_change_me_please_change_me_please_32_chars");
    var roles = MergeRoles(user.Role, featureRoles);
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Email, user.Email),
        new("role", user.Role),
        new("roles", string.Join(',', roles)),
        new("primary_role", user.Role),
        new("token_type", tokenType),
        new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };
    foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
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
public sealed record ProfileUpdateRequest(string? FirstName, string? LastName, string? PhoneNumber, string? ProfilePictureUrl, string? AdditionalDataJson);
public sealed record PublicProfileExtra(string? Bio, string? Location, string? Education, string? Github, string? Telegram, string? Website, IReadOnlyList<string> Skills, bool ShowInLeaderboard)
{
    public static PublicProfileExtra Empty { get; } = new(null, null, null, null, null, null, Array.Empty<string>(), true);
}
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record ChangeEmailRequest(string? NewEmail, string? Password);
public sealed record RevealEmailRequest(string? Password);
public sealed record AdminUserUpdateRequest(string? Email, string? FirstName, string? LastName, string? PhoneNumber, string? ProfilePictureUrl, string? Role);

public sealed record RoleAssignRequest(string? Code);
public sealed record FeatureRoleRequest(string? Code, string? Title, string? Description, bool? IsActive);
