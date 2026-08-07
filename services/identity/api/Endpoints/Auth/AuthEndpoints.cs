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
            if (await CheckAuthRateLimitAsync(http, "register", request.Login ?? request.Email) is { } limited) return limited;

            var login = NormalizeLogin(request.Login);
            if (!IsValidLogin(login, out var loginMessage)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = loginMessage });

            var email = NormalizeOptionalEmail(request.Email);
            if (!string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(email))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Email указан в неверном формате." });

            if (!IsValidPassword(request.Password, out var passwordMessage)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = passwordMessage });
            if (!TryNormalizeAccountType(request.AccountType, out var accountType)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "accountType должен быть human или ai.", code = "INVALID_ACCOUNT_TYPE" });
            if (await db.Users.AnyAsync(x => x.Login == login)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Пользователь с таким логином уже существует." });
            if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(x => x.Email == email)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Пользователь с таким email уже существует." });

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
                Role = role,
                AccountType = accountType
            };
            db.Users.Add(user);
            db.UiSettings.Add(new UserUiSettings { UserId = user.Id, DataJson = DefaultUiSettingsJson() });
            await db.SaveChangesAsync();

            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "Пользователь зарегистрирован", userId = user.Id, login = user.Login, role = user.Role, accountType = user.AccountType, isAi = user.AccountType == "ai" });
        });

        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            if (await CheckAuthRateLimitAsync(http, "login", request.Login ?? request.Email) is { } limited) return limited;

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

            if (!string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    message = user.AccountStatus == "merged"
                        ? "Этот аккаунт объединён с другим. Используйте основной аккаунт."
                        : "Этот аккаунт удалён и больше недоступен.",
                    code = user.AccountStatus == "merged" ? "ACCOUNT_MERGED" : "ACCOUNT_DELETED"
                }, statusCode: StatusCodes.Status423Locked);
            }

            var loginBlock = await db.BlockedAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (loginBlock != null && (!loginBlock.ExpiresAtUtc.HasValue || loginBlock.ExpiresAtUtc > DateTimeOffset.UtcNow))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    message = "Аккаунт заблокирован администратором.",
                    code = "ACCOUNT_BLOCKED",
                    reason = loginBlock.Reason,
                    expiresAtUtc = loginBlock.ExpiresAtUtc
                }, statusCode: StatusCodes.Status423Locked);
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
                UserAgent = http.Request.Headers.UserAgent.ToString(),
                DeviceHash = ResolveDeviceHash(http, cfg)
            });
            await db.SaveChangesAsync();

            var accessLifetime = TimeSpan.FromMinutes(cfg.GetValue<int?>("Jwt:ExpireMinutes") ?? 120);
            var refreshLifetime = TimeSpan.FromDays(7);
            var roles = await RolesForUser(db, user);
            var access = CreateJwt(user, cfg, accessLifetime, "access", roles);
            var refresh = CreateJwt(user, cfg, refreshLifetime, "refresh", roles);
            SetAuthCookies(http, access, refresh, accessLifetime, refreshLifetime);
            return Microsoft.AspNetCore.Http.Results.Ok(new { accessToken = access, user = ToProfile(user, roles) });
        });

        app.MapPost("/api/auth/refresh", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            if (await CheckAuthRateLimitAsync(http, "refresh") is { } limited) return limited;
            var principal = ValidateToken(ReadCookie(http, "tf_rt"), cfg, validateLifetime: true);
            var uid = principal == null ? null : TryGetUserId(principal);
            if (uid == null) return Unauthorized("Сессия истекла. Войдите заново.");
            if (!string.Equals(principal!.FindFirstValue("token_type"), "refresh", StringComparison.OrdinalIgnoreCase)) return Unauthorized("Сессия истекла. Войдите заново.");

            var user = await db.Users.FindAsync(uid.Value);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            if (!string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
            {
                ClearAuthCookies(http);
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    message = "Аккаунт больше недоступен.",
                    code = user.AccountStatus == "merged" ? "ACCOUNT_MERGED" : "ACCOUNT_DELETED"
                }, statusCode: StatusCodes.Status423Locked);
            }
            var refreshBlock = await db.BlockedAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (refreshBlock != null && (!refreshBlock.ExpiresAtUtc.HasValue || refreshBlock.ExpiresAtUtc > DateTimeOffset.UtcNow))
            {
                ClearAuthCookies(http);
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    message = "Аккаунт заблокирован администратором.",
                    code = "ACCOUNT_BLOCKED",
                    reason = refreshBlock.Reason,
                    expiresAtUtc = refreshBlock.ExpiresAtUtc
                }, statusCode: StatusCodes.Status423Locked);
            }

            var accessLifetime = TimeSpan.FromMinutes(cfg.GetValue<int?>("Jwt:ExpireMinutes") ?? 120);
            var refreshLifetime = TimeSpan.FromDays(7);
            var roles = await RolesForUser(db, user);
            var access = CreateJwt(user, cfg, accessLifetime, "access", roles);
            var refresh = CreateJwt(user, cfg, refreshLifetime, "refresh", roles);
            SetAuthCookies(http, access, refresh, accessLifetime, refreshLifetime);
            return Microsoft.AspNetCore.Http.Results.Ok(new { accessToken = access, user = ToProfile(user, roles) });
        });

        app.MapPost("/api/auth/logout", (HttpContext http) =>
        {
            ClearAuthCookies(http);
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "ok" });
        });

        MapPasswordRecoveryEndpoints(app);

        return app;
    }
}
