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

using TaskForge.Identity.Api.Contracts;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private static WebApplication MapAuthEndpoints(WebApplication app)
    {
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

        return app;
    }
}
