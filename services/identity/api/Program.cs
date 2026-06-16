using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("identity-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "identity-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<IdentityDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("identity-api");

ValidateProductionIdentityConfig(app.Configuration, app.Environment);

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for IdentityDbContext...");
    await db.Database.MigrateAsync();
    await BackfillUserLoginsAsync(db, app.Logger);
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

app.MapPost("/api/auth/register", async (RegisterRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    if (CheckAuthRateLimit(http, "register", request.Login ?? request.Email) is { } limited) return limited;

    var login = NormalizeLogin(request.Login);
    if (!IsValidLogin(login, out var loginMessage)) return Results.BadRequest(new { message = loginMessage });

    var email = NormalizeOptionalEmail(request.Email);
    if (!string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(email))
        return Results.BadRequest(new { message = "Email указан в неверном формате." });

    if (!IsValidPassword(request.Password, out var passwordMessage)) return Results.BadRequest(new { message = passwordMessage });
    if (await db.Users.AnyAsync(x => x.Login == login)) return Results.BadRequest(new { message = "Пользователь с таким логином уже существует." });
    if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(x => x.Email == email)) return Results.BadRequest(new { message = "Пользователь с таким email уже существует." });

    var firstUser = !await db.Users.AnyAsync();
    var role = ResolveInitialRole(email ?? string.Empty, firstUser, cfg);
    var salt = NewSalt();
    var user = new IdentityUser
    {
        Login = login,
        Email = email,
        FirstName = (request.FirstName ?? string.Empty).Trim(),
        LastName = (request.LastName ?? string.Empty).Trim(),
        PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim(),
        AdditionalDataJson = string.IsNullOrWhiteSpace(request.AdditionalDataJson) ? null : request.AdditionalDataJson,
        PasswordSalt = salt,
        PasswordHash = HashPassword(request.Password ?? string.Empty, salt),
        Role = role
    };
    db.Users.Add(user);
    db.UiSettings.Add(new UserUiSettings { UserId = user.Id, DataJson = DefaultUiSettingsJson() });
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Пользователь зарегистрирован", userId = user.Id, login = user.Login, role = user.Role });
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    if (CheckAuthRateLimit(http, "login", request.Login ?? request.Email) is { } limited) return limited;

    var identity = (request.Login ?? request.Email ?? string.Empty).Trim();
    var identityIsEmail = identity.Contains('@');
    var email = NormalizeOptionalEmail(request.Email);
    if (email == null && identityIsEmail) email = NormalizeOptionalEmail(identity);

    var login = identityIsEmail ? string.Empty : NormalizeLogin(identity);
    IdentityUser? user = null;
    if (!string.IsNullOrWhiteSpace(email))
    {
        user = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
    }
    if (user == null && !string.IsNullOrWhiteSpace(login))
    {
        user = await db.Users.FirstOrDefaultAsync(x => x.Login == login);
    }

    if (user == null || !VerifyPassword(request.Password ?? string.Empty, user.PasswordSalt, user.PasswordHash))
    {
        return Unauthorized("Неверный логин/email или пароль. Проверьте данные или зарегистрируйтесь.", "INVALID_CREDENTIALS");
    }

    if (NeedsPasswordRehash(user.PasswordHash))
    {
        var freshSalt = NewSalt();
        user.PasswordSalt = freshSalt;
        user.PasswordHash = HashPassword(request.Password ?? string.Empty, freshSalt);
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
    if (CheckAuthRateLimit(http, "refresh") is { } limited) return limited;
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

    if (request.Login != null)
    {
        var login = NormalizeLogin(request.Login);
        if (!IsValidLogin(login, out var loginMessage)) return Results.BadRequest(new { message = loginMessage });
        if (await db.Users.AnyAsync(x => x.Login == login && x.Id != user.Id)) return Results.BadRequest(new { message = "Логин уже занят" });
        user.Login = login;
    }

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
    if (CheckAuthRateLimit(http, "password") is { } limited) return limited;
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    if (!VerifyPassword(request.CurrentPassword ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Results.BadRequest(new { message = "Неверный текущий пароль" });
    if (!IsValidPassword(request.NewPassword, out var passwordMessage)) return Results.BadRequest(new { message = passwordMessage });
    var salt = NewSalt();
    user.PasswordSalt = salt;
    user.PasswordHash = HashPassword(request.NewPassword ?? string.Empty, salt);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Пароль изменён" });
});

app.MapPost("/api/profile/change-email", async (ChangeEmailRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    if (CheckAuthRateLimit(http, "password") is { } limited) return limited;
    var user = await FindCurrentUserAsync(http, db, cfg);
    if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
    if (!VerifyPassword(request.Password ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Results.BadRequest(new { message = "Неверный пароль" });
    var email = NormalizeOptionalEmail(request.NewEmail);
    if (!string.IsNullOrWhiteSpace(request.NewEmail) && string.IsNullOrWhiteSpace(email)) return Results.BadRequest(new { message = "Email указан в неверном формате" });
    if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(x => x.Email == email && x.Id != user.Id)) return Results.BadRequest(new { message = "Email уже занят" });
    user.Email = email;
    await db.SaveChangesAsync();
    return Results.Ok(ToProfile(user, await RolesForUser(db, user)));
});

app.MapPost("/api/profile/reveal-email", async (RevealEmailRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
{
    if (CheckAuthRateLimit(http, "password") is { } limited) return limited;
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

app.MapGet("/api/users/{userId:guid}/public-profile", async (Guid userId, IdentityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
    if (user == null) return Results.NotFound(new { message = "Профиль не найден" });

    var extra = ReadPublicProfileExtra(user.AdditionalDataJson);
    if (!extra.PublicProfileEnabled) return Results.NotFound(new { message = "Профиль не найден" });

    var stats = extra.ShowStats ? await FetchUserActivitySummaryAsync(userId, cfg, httpFactory, ct) : ActivitySummaryDto.Empty;
    return Results.Ok(new
    {
        user.Id,
        login = UserLoginOrFallback(user),
        user.FirstName,
        user.LastName,
        avatarUrl = user.ProfilePictureUrl,
        profilePictureUrl = user.ProfilePictureUrl,
        displayName = PublicDisplayName(user),
        user.CreatedAt,
        bio = extra.ShowBio ? extra.Bio : null,
        location = extra.ShowLocation ? extra.Location : null,
        education = extra.ShowEducation ? extra.Education : null,
        github = extra.ShowGithub ? extra.Github : null,
        telegram = extra.ShowTelegram ? extra.Telegram : null,
        website = extra.ShowWebsite ? extra.Website : null,
        skills = extra.ShowSkills ? extra.Skills : Array.Empty<string>(),
        showInLeaderboard = extra.ShowInLeaderboard,
        statsVisible = extra.ShowStats,
        solvedAssignments = extra.ShowStats ? stats.SolvedAssignments : (int?)null,
        totalAttempts = extra.ShowStats ? stats.TotalAttempts : (int?)null,
        codeSolutions = extra.ShowStats ? stats.CodeSolutions : (int?)null,
        imageSolutions = extra.ShowStats ? stats.ImageSolutions : (int?)null,
        testAttempts = extra.ShowStats ? stats.TestAttempts : (int?)null,
        mathAttempts = extra.ShowStats ? stats.MathAttempts : (int?)null
    });
});

app.MapPost("/api/internal/users/summaries", async (UserIdsRequest request, IdentityDbContext db, IDistributedCache cache, IConfiguration cfg, ILogger<Program> logger, CancellationToken ct) =>
{
    var ids = (request.UserIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(1000).OrderBy(x => x).ToArray();
    if (ids.Length == 0)
    {
        TaskForgeDebugTrace.UserSummaryServed("identity-api", ids, Array.Empty<UserSummaryDto>());
        return Results.Ok(Array.Empty<UserSummaryDto>());
    }

    var key = TaskForgeCache.Key("identity:user-summaries:v3", ids);
    var ttl = TaskForgeCache.Ttl(cfg, "UserSummaries", 120);
    var result = await TaskForgeCache.GetOrSetAsync(cache, cfg, logger, key, ttl, async token =>
    {
        TaskForgeDebugTrace.UserSummaryRequest("identity-api", "identity-db", ids);
        var rows = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(token);
        return rows.Select(ToUserSummaryDto).ToList();
    }, ct);

    TaskForgeDebugTrace.UserSummaryServed("identity-api", ids, result);
    return Results.Ok(result);
});


app.MapGet("/api/admin/solution-users", async (IdentityDbContext db, string? q, int take = 50) =>
{
    var rows = await SearchUsersAsync(db, q, null, false, "login", "asc", Math.Clamp(take, 1, 200));
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
    var rows = await SearchUsersAsync(db, query ?? q, role, linkedOnly, sortBy, sortDir, Math.Clamp(take, 1, 500));
    var total = await CountUsersAsync(db, query ?? q, role, linkedOnly);
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
    if (!string.IsNullOrWhiteSpace(request.Login))
    {
        var login = NormalizeLogin(request.Login);
        if (!IsValidLogin(login, out var loginMessage)) return Results.BadRequest(new { message = loginMessage });
        if (await db.Users.AnyAsync(x => x.Login == login && x.Id != user.Id)) return Results.BadRequest(new { message = "Логин уже занят" });
        user.Login = login;
    }
    if (request.Email != null)
    {
        var email = NormalizeOptionalEmail(request.Email);
        if (!string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(email)) return Results.BadRequest(new { message = "Email указан в неверном формате" });
        if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(x => x.Email == email && x.Id != user.Id)) return Results.BadRequest(new { message = "Email уже занят" });
        user.Email = email;
    }
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
    var rows = await SearchUsersAsync(db, query, null, false, "login", "asc", Math.Clamp(limit, 1, 200));
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

static IResult? CheckAuthRateLimit(HttpContext http, string bucket, string? identity = null)
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var normalizedIdentity = string.IsNullOrWhiteSpace(identity) ? "none" : (NormalizeOptionalEmail(identity) ?? NormalizeLogin(identity));
    var key = $"{bucket}:{ip}:{normalizedIdentity}";
    if (TaskForgeAuthRateLimiters.Allow(bucket, key)) return null;

    return Results.Json(new
    {
        message = "Слишком много попыток. Подождите немного и попробуйте снова.",
        code = "RATE_LIMITED"
    }, statusCode: StatusCodes.Status429TooManyRequests);
}



static IQueryable<IdentityUser> FilterUsers(IQueryable<IdentityUser> query, string? text, string? role, bool linkedOnly)
{
    if (!string.IsNullOrWhiteSpace(text))
    {
        var q = text.Trim().ToLowerInvariant();
        query = query.Where(x => ((x.Login != null && x.Login.ToLower().Contains(q)) || (x.Email != null && x.Email.ToLower().Contains(q)) || x.FirstName.ToLower().Contains(q)) || x.LastName.ToLower().Contains(q));
    }
    if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role == role.Trim());
    if (linkedOnly) query = query.Where(x => false);
    return query;
}

static async Task<List<IdentityUser>> SearchUsersAsync(IdentityDbContext db, string? text, string? role, bool linkedOnly, string? sortBy, string? sortDir, int take)
{
    var query = db.Users.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role == role.Trim());
    if (linkedOnly) return new List<IdentityUser>();

    var q = NormalizeSearch(text);
    if (string.IsNullOrWhiteSpace(q))
    {
        return await SortUsers(query, sortBy, sortDir).Take(Math.Clamp(take, 1, 500)).ToListAsync();
    }

    var rows = await query.Take(5000).ToListAsync();
    var maxDistance = Math.Max(1, Math.Min(4, q.Length / 3));
    return rows
        .Select(u => new { User = u, Score = UserSearchScore(u, q) })
        .Where(x => x.Score <= maxDistance || UserSearchHaystack(x.User).Contains(q, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Score)
        .ThenBy(x => x.User.Login ?? x.User.Email ?? x.User.Id.ToString())
        .Take(Math.Clamp(take, 1, 500))
        .Select(x => x.User)
        .ToList();
}

static async Task<int> CountUsersAsync(IdentityDbContext db, string? text, string? role, bool linkedOnly)
{
    if (linkedOnly) return 0;
    if (string.IsNullOrWhiteSpace(text))
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role == role.Trim());
        return await query.CountAsync();
    }
    return (await SearchUsersAsync(db, text, role, linkedOnly, "login", "asc", 5000)).Count;
}

static int UserSearchScore(IdentityUser user, string query)
{
    var values = new[]
    {
        user.Login ?? string.Empty,
        user.Email ?? string.Empty,
        user.FirstName,
        user.LastName,
        DisplayName(user),
        user.Id.ToString()
    }
    .Select(NormalizeSearch)
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .ToArray();

    if (values.Any(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))) return 0;

    var best = int.MaxValue;
    foreach (var value in values)
    {
        best = Math.Min(best, Levenshtein(value, query));
        foreach (var token in value.Split(new[] { ' ', '@', '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            best = Math.Min(best, Levenshtein(token, query));
        }
    }
    return best == int.MaxValue ? 999 : best;
}

static string UserSearchHaystack(IdentityUser user)
    => NormalizeSearch($"{user.Login} {user.Email} {user.FirstName} {user.LastName} {DisplayName(user)} {user.Id}");

static string NormalizeSearch(string? value)
    => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

static async Task<ActivitySummaryDto> FetchUserActivitySummaryAsync(Guid userId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var solutions = await FetchActivitySummaryFromAsync(ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080"), userId, cfg, httpFactory, ct);
    var tasks = await FetchActivitySummaryFromAsync(ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080"), userId, cfg, httpFactory, ct);
    return new ActivitySummaryDto(
        solutions.SolvedAssignments + tasks.SolvedAssignments,
        solutions.TotalAttempts + tasks.TotalAttempts,
        solutions.CodeSolutions,
        solutions.ImageSolutions,
        tasks.TestAttempts,
        tasks.MathAttempts);
}

static async Task<ActivitySummaryDto> FetchActivitySummaryFromAsync(string baseUrl, Guid userId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    try
    {
        var client = httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/internal/users/{userId}/activity-summary");
        AddInternalKey(msg, cfg);
        using var resp = await client.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) return ActivitySummaryDto.Empty;
        return await resp.Content.ReadFromJsonAsync<ActivitySummaryDto>(JsonOptions(), ct) ?? ActivitySummaryDto.Empty;
    }
    catch
    {
        return ActivitySummaryDto.Empty;
    }
}

static string ServiceUrl(IConfiguration cfg, string name, string fallback)
    => (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
{
    var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
    if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
}

static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };
static IQueryable<IdentityUser> SortUsers(IQueryable<IdentityUser> query, string? sortBy, string? sortDir)
{
    var desc = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
    return (sortBy ?? "createdAt") switch
    {
        "login" => desc ? query.OrderByDescending(x => x.Login) : query.OrderBy(x => x.Login),
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
    login = UserLoginOrFallback(user),
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
    mathSolutions = 0,
    integrationDataReliable = false,
    solutionStatsReliable = false
};
static UserSummaryDto ToUserSummaryDto(IdentityUser user)
{
    var extra = ReadPublicProfileExtra(user.AdditionalDataJson);
    return new UserSummaryDto(
        user.Id,
        user.Id,
        UserLoginOrFallback(user),
        user.Email ?? string.Empty,
        MaskEmail(user.Email),
        user.FirstName,
        user.LastName,
        user.ProfilePictureUrl,
        user.ProfilePictureUrl,
        DisplayName(user),
        DisplayName(user),
        user.Role,
        extra.ShowLocation ? extra.Location : null,
        extra.ShowEducation ? extra.Education : null,
        extra.ShowInLeaderboard,
        user.CreatedAt,
        user.LastLoginAt);
}
static string NormalizeRole(string? role) => (role ?? "User").Trim() switch
{
    "Admin" => "Admin",
    "Editor" => "Editor",
    "LearningEditor" => "LearningEditor",
    "Minecraft" => "Minecraft",
    _ => "User"
};
static string UserLoginOrFallback(IdentityUser user)
    => !string.IsNullOrWhiteSpace(user.Login) ? user.Login! : user.Email ?? user.Id.ToString();

static async Task BackfillUserLoginsAsync(IdentityDbContext db, ILogger logger)
{
    var users = await db.Users.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
    if (users.Count == 0) return;

    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var user in users)
    {
        var normalized = NormalizeLogin(user.Login);
        if (!string.IsNullOrWhiteSpace(normalized) && IsValidLogin(normalized, out _))
        {
            user.Login = normalized;
            used.Add(normalized);
        }
        else if (!string.IsNullOrWhiteSpace(user.Login))
        {
            user.Login = null;
        }
    }

    var changed = 0;
    foreach (var user in users.Where(x => string.IsNullOrWhiteSpace(x.Login)))
    {
        var login = BuildBackfilledLogin(user, used);
        user.Login = login;
        used.Add(login);
        changed++;
    }

    if (changed > 0)
    {
        await db.SaveChangesAsync();
        logger.LogInformation("Backfilled {Count} missing user logins.", changed);
    }
}

static string BuildBackfilledLogin(IdentityUser user, HashSet<string> used)
{
    var basePart = NormalizeLogin(user.Email);
    if (!IsValidLogin(basePart, out _)) basePart = "user";

    var suffix = user.Id.ToString("N")[..8].ToLowerInvariant();
    var maxBaseLength = Math.Max(3, 64 - suffix.Length - 1);
    if (basePart.Length > maxBaseLength) basePart = basePart[..maxBaseLength].Trim('.', '-', '_');
    if (!IsValidLogin(basePart, out _)) basePart = "user";

    var candidate = $"{basePart}-{suffix}";
    var counter = 2;
    while (used.Contains(candidate))
    {
        var counterSuffix = $"{suffix}-{counter}";
        maxBaseLength = Math.Max(3, 64 - counterSuffix.Length - 1);
        var trimmedBase = basePart.Length > maxBaseLength ? basePart[..maxBaseLength].Trim('.', '-', '_') : basePart;
        if (!IsValidLogin(trimmedBase, out _)) trimmedBase = "user";
        candidate = $"{trimmedBase}-{counterSuffix}";
        counter++;
    }

    return candidate;
}

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

static void ValidateProductionIdentityConfig(IConfiguration cfg, IHostEnvironment env)
{
    if (!env.IsProduction()) return;

    var jwt = cfg["Jwt:Key"] ?? cfg["Jwt:SigningKey"];
    if (IsUnsafeProductionSecret(jwt, minLength: 48))
    {
        throw new InvalidOperationException("Production JWT signing key is missing, weak or still uses a placeholder.");
    }

    var bootstrapAdminEmails = (cfg["Bootstrap:AdminEmails"] ?? string.Empty).Trim();
    var firstUserIsAdmin = cfg.GetValue("Bootstrap:FirstUserIsAdmin", false);
    if (firstUserIsAdmin && string.IsNullOrWhiteSpace(bootstrapAdminEmails))
    {
        throw new InvalidOperationException("Production must not rely on first-user-is-admin without explicit Bootstrap:AdminEmails.");
    }
}

static bool IsUnsafeProductionSecret(string? value, int minLength)
{
    var v = (value ?? string.Empty).Trim();
    if (v.Length < minLength) return true;
    if (v.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)) return true;
    if (v.Contains("dev_change_me", StringComparison.OrdinalIgnoreCase)) return true;
    if (v.Contains("password", StringComparison.OrdinalIgnoreCase)) return true;
    if (v.Distinct().Count() < 8) return true;
    return false;
}

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
static string? NormalizeOptionalEmail(string? email)
{
    var value = (email ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(value)) return null;
    var at = value.IndexOf('@');
    var lastAt = value.LastIndexOf('@');
    if (at <= 0 || at != lastAt || at >= value.Length - 3 || !value[(at + 1)..].Contains('.')) return null;
    return value.Length <= 320 ? value : null;
}

static string NormalizeEmail(string? email) => NormalizeOptionalEmail(email) ?? string.Empty;

static string NormalizeLogin(string? login)
{
    var raw = (login ?? string.Empty).Trim().ToLowerInvariant();
    if (raw.Contains('@')) raw = raw.Split('@', 2)[0];

    var sb = new StringBuilder(raw.Length);
    var prevDash = false;
    foreach (var ch in raw)
    {
        var allowed = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == '-';
        if (allowed)
        {
            sb.Append(ch);
            prevDash = ch == '-';
        }
        else if (!prevDash)
        {
            sb.Append('-');
            prevDash = true;
        }
    }

    return sb.ToString().Trim('.', '-', '_');
}

static bool IsValidLogin(string? login, out string message)
{
    var value = (login ?? string.Empty).Trim();
    if (value.Length < 3)
    {
        message = "Логин должен быть не короче 3 символов.";
        return false;
    }
    if (value.Length > 64)
    {
        message = "Логин должен быть не длиннее 64 символов.";
        return false;
    }
    if (value.Any(ch => !((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == '-')))
    {
        message = "Логин может содержать только латинские буквы, цифры, точку, дефис и подчёркивание.";
        return false;
    }
    message = string.Empty;
    return true;
}

static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

static bool IsValidPassword(string? password, out string message)
{
    var value = password ?? string.Empty;
    if (value.Length < 8)
    {
        message = "Пароль должен быть не короче 8 символов.";
        return false;
    }

    if (value.Length > 256)
    {
        message = "Пароль слишком длинный.";
        return false;
    }

    if (value.All(char.IsWhiteSpace))
    {
        message = "Пароль не может состоять только из пробелов.";
        return false;
    }

    message = string.Empty;
    return true;
}

static string HashPassword(string password, string salt)
{
    const int iterations = 210_000;
    var saltBytes = Convert.FromBase64String(salt);
    var hash = Rfc2898DeriveBytes.Pbkdf2(
        password: Encoding.UTF8.GetBytes(password ?? string.Empty),
        salt: saltBytes,
        iterations: iterations,
        hashAlgorithm: HashAlgorithmName.SHA256,
        outputLength: 32);
    return $"PBKDF2-SHA256${iterations}${Convert.ToBase64String(hash)}";
}

static bool VerifyPassword(string password, string salt, string hash)
{
    try
    {
        if (hash.StartsWith("PBKDF2-SHA256$", StringComparison.Ordinal))
        {
            var parts = hash.Split('$');
            if (parts.Length != 3 || !int.TryParse(parts[1], out var iterations) || iterations < 100_000) return false;
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password: Encoding.UTF8.GetBytes(password ?? string.Empty),
                salt: saltBytes,
                iterations: iterations,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: expected.Length);
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        // Legacy v17 and older used SHA256(salt + ":" + password). Keep read support and migrate on successful login.
        using var sha = SHA256.Create();
        var legacy = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + (password ?? string.Empty))));
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(legacy), Convert.FromBase64String(hash));
    }
    catch
    {
        return false;
    }
}

static bool NeedsPasswordRehash(string? hash) => string.IsNullOrWhiteSpace(hash) || !hash.StartsWith("PBKDF2-SHA256$", StringComparison.Ordinal);
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
static string PublicDisplayName(IdentityUser user) => DisplayName(user);
static string DisplayName(IdentityUser user)
{
    var full = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    if (!string.IsNullOrWhiteSpace(full)) return full;
    if (!string.IsNullOrWhiteSpace(user.Login)) return user.Login!;
    return "Пользователь";
}
static object ToProfile(IdentityUser user, IReadOnlyCollection<string>? featureRoles = null) => new
{
    user.Id,
    login = UserLoginOrFallback(user),
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

        var showInLeaderboard = ReadJsonBool(root, "showInLeaderboard", true);

        return new PublicProfileExtra(
            PublicProfileEnabled: ReadJsonBool(root, "publicProfileEnabled", true),
            Bio: ReadJsonString(root, "bio"),
            Location: ReadJsonString(root, "location"),
            Education: ReadJsonString(root, "education"),
            Github: ReadJsonString(links, "github"),
            Telegram: ReadJsonString(links, "telegram"),
            Website: ReadJsonString(links, "website"),
            Skills: skills,
            ShowInLeaderboard: showInLeaderboard,
            ShowBio: ReadJsonBool(root, "showBio", false),
            ShowLocation: ReadJsonBool(root, "showLocation", false),
            ShowEducation: ReadJsonBool(root, "showEducation", false),
            ShowGithub: ReadJsonBool(root, "showGithub", false),
            ShowTelegram: ReadJsonBool(root, "showTelegram", false),
            ShowWebsite: ReadJsonBool(root, "showWebsite", false),
            ShowSkills: ReadJsonBool(root, "showSkills", false),
            ShowStats: ReadJsonBool(root, "showStats", false)
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
static bool ReadJsonBool(JsonElement element, string propertyName, bool defaultValue)
{
    if (element.ValueKind != JsonValueKind.Object) return defaultValue;
    if (!element.TryGetProperty(propertyName, out var value)) return defaultValue;
    return value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => defaultValue
    };
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
        new(ClaimTypes.Name, UserLoginOrFallback(user)),
        new("login", UserLoginOrFallback(user)),
        new(ClaimTypes.Email, user.Email ?? string.Empty),
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

public sealed record RegisterRequest(string? Login, string? Email, string? Password, string? FirstName, string? LastName, string? PhoneNumber, string? AdditionalDataJson);
public sealed record LoginRequest(string? Login, string? Email, string? Password);
public sealed record ProfileUpdateRequest(string? Login, string? FirstName, string? LastName, string? PhoneNumber, string? ProfilePictureUrl, string? AdditionalDataJson);
public sealed record PublicProfileExtra(
    bool PublicProfileEnabled,
    string? Bio,
    string? Location,
    string? Education,
    string? Github,
    string? Telegram,
    string? Website,
    IReadOnlyList<string> Skills,
    bool ShowInLeaderboard,
    bool ShowBio,
    bool ShowLocation,
    bool ShowEducation,
    bool ShowGithub,
    bool ShowTelegram,
    bool ShowWebsite,
    bool ShowSkills,
    bool ShowStats)
{
    public static PublicProfileExtra Empty { get; } = new(true, null, null, null, null, null, null, Array.Empty<string>(), true, false, false, false, false, false, false, false, false);
}
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record ChangeEmailRequest(string? NewEmail, string? Password);
public sealed record RevealEmailRequest(string? Password);
public sealed record AdminUserUpdateRequest(string? Login, string? Email, string? FirstName, string? LastName, string? PhoneNumber, string? ProfilePictureUrl, string? Role);

public sealed record RoleAssignRequest(string? Code);
public sealed record FeatureRoleRequest(string? Code, string? Title, string? Description, bool? IsActive);

public sealed record UserIdsRequest(Guid[]? UserIds);
public sealed record ActivitySummaryDto(int SolvedAssignments, int TotalAttempts, int CodeSolutions, int ImageSolutions, int TestAttempts, int MathAttempts)
{
    public static ActivitySummaryDto Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public sealed record UserSummaryDto(
    Guid Id,
    Guid UserId,
    string Login,
    string Email,
    string MaskedEmail,
    string FirstName,
    string LastName,
    string? AvatarUrl,
    string? ProfilePictureUrl,
    string DisplayName,
    string FullName,
    string Role,
    string? Location,
    string? Education,
    bool ShowInLeaderboard,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);


internal static class TaskForgeAuthRateLimiters
{
    private static readonly SlidingWindowRateLimiter Limiter = new();

    public static bool Allow(string bucket, string key)
    {
        var (limit, window) = bucket switch
        {
            "login" => (12, TimeSpan.FromMinutes(5)),
            "register" => (5, TimeSpan.FromMinutes(10)),
            "refresh" => (120, TimeSpan.FromMinutes(5)),
            "password" => (8, TimeSpan.FromMinutes(10)),
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
