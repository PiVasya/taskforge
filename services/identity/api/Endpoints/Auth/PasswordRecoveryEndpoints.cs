using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Identity.Api.Contracts;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private const string PasswordRecoveryChallengeCookie = "tf_prc";
    private const string PasswordRecoveryResetCookie = "tf_prt";

    private static readonly HttpClient PasswordRecoveryTelegramClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 20
    })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static WebApplication MapPasswordRecoveryEndpoints(WebApplication app)
    {
        app.MapGet("/api/auth/password-recovery/status", async (
            HttpContext http,
            IdentityDbContext db,
            IDistributedCache cache,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var resetToken = ReadCookie(http, PasswordRecoveryResetCookie);
            if (!string.IsNullOrWhiteSpace(resetToken))
            {
                var grantKey = PasswordRecoveryGrantKey(resetToken);
                var grant = DeserializePasswordRecoveryGrant(await cache.GetStringAsync(grantKey, ct));
                if (grant != null && grant.ExpiresAtUtc > now)
                {
                    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == grant.UserId, ct);
                    return Microsoft.AspNetCore.Http.Results.Ok(new
                    {
                        stage = "password",
                        identity = user == null ? null : UserLoginOrFallback(user),
                        expiresInSeconds = Math.Max(1, (int)Math.Ceiling((grant.ExpiresAtUtc - now).TotalSeconds))
                    });
                }

                await cache.RemoveAsync(grantKey, ct);
                DeletePasswordRecoveryCookie(http, PasswordRecoveryResetCookie);
            }

            var challengeId = ReadCookie(http, PasswordRecoveryChallengeCookie);
            if (!string.IsNullOrWhiteSpace(challengeId))
            {
                var challengeKey = PasswordRecoveryChallengeKey(challengeId);
                var challenge = DeserializePasswordRecoveryChallenge(await cache.GetStringAsync(challengeKey, ct));
                if (challenge != null && challenge.ExpiresAtUtc > now)
                {
                    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == challenge.UserId, ct);
                    return Microsoft.AspNetCore.Http.Results.Ok(new
                    {
                        stage = "code",
                        identity = user == null ? null : UserLoginOrFallback(user),
                        expiresInSeconds = Math.Max(1, (int)Math.Ceiling((challenge.ExpiresAtUtc - now).TotalSeconds)),
                        botUsername = TelegramBotUsername(cfg)
                    });
                }

                await cache.RemoveAsync(challengeKey, ct);
                DeletePasswordRecoveryCookie(http, PasswordRecoveryChallengeCookie);
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new { stage = "identity" });
        });

        app.MapPost("/api/auth/password-recovery/cancel", async (
            HttpContext http,
            IDistributedCache cache,
            CancellationToken ct) =>
        {
            var resetToken = ReadCookie(http, PasswordRecoveryResetCookie);
            if (!string.IsNullOrWhiteSpace(resetToken))
            {
                await cache.RemoveAsync(PasswordRecoveryGrantKey(resetToken), ct);
            }

            var challengeId = ReadCookie(http, PasswordRecoveryChallengeCookie);
            if (!string.IsNullOrWhiteSpace(challengeId))
            {
                var challengeKey = PasswordRecoveryChallengeKey(challengeId);
                var challenge = DeserializePasswordRecoveryChallenge(await cache.GetStringAsync(challengeKey, ct));
                await cache.RemoveAsync(challengeKey, ct);
                if (challenge != null)
                {
                    await cache.RemoveAsync(PasswordRecoveryUserChallengeKey(challenge.UserId), ct);
                }
            }

            ClearPasswordRecoveryCookies(http);
            return Microsoft.AspNetCore.Http.Results.Ok(new { cancelled = true });
        });

        app.MapPost("/api/auth/password-recovery/request", async (
            PasswordRecoveryRequest request,
            HttpContext http,
            IdentityDbContext db,
            IDistributedCache cache,
            IConfiguration cfg,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var identity = (request.Identity ?? string.Empty).Trim();
            if (CheckAuthRateLimit(http, "password-recovery-request", identity) is { } limited) return limited;
            ClearPasswordRecoveryCookies(http);

            if (string.IsNullOrWhiteSpace(identity))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Укажите логин или email аккаунта."
                });
            }

            var user = await FindPasswordRecoveryUserAsync(db, identity, ct);
            if (user == null || !string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
            {
                return PasswordRecoveryUnavailable();
            }

            var activeBlock = await db.BlockedAccounts.AsNoTracking()
                .AnyAsync(x => x.UserId == user.Id && (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > DateTimeOffset.UtcNow), ct);
            if (activeBlock)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    available = false,
                    message = "Извините, автоматическое восстановление этого аккаунта невозможно.",
                    code = "RECOVERY_NOT_AVAILABLE"
                });
            }

            if (user.TelegramChatId == null)
            {
                return PasswordRecoveryUnavailable();
            }

            var botToken = PasswordRecoveryBotToken(cfg);
            if (string.IsNullOrWhiteSpace(botToken))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    available = false,
                    message = "Сервис восстановления через Telegram сейчас недоступен. Попробуйте позже.",
                    code = "RECOVERY_DELIVERY_UNAVAILABLE"
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var now = DateTimeOffset.UtcNow;
            var throttle = await TryConsumeTelegramThrottleAsync(
                cache,
                $"password-recovery:{user.Id:N}",
                PasswordRecoveryIssueLimit(cfg),
                TimeSpan.FromMinutes(PasswordRecoveryIssueWindowMinutes(cfg)),
                now,
                ct);
            if (!throttle.Allowed)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    message = $"Код уже отправлялся недавно. Подождите {throttle.RetryAfterSeconds} сек. и попробуйте снова.",
                    code = "RECOVERY_RATE_LIMITED",
                    retryAfterSeconds = throttle.RetryAfterSeconds
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var challengeId = NewOpaquePasswordRecoveryToken();
            var verificationCode = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            var lifetime = TimeSpan.FromMinutes(PasswordRecoveryCodeLifetimeMinutes(cfg));
            var expiresAt = now.Add(lifetime);
            var challengeKey = PasswordRecoveryChallengeKey(challengeId);
            var userChallengeKey = PasswordRecoveryUserChallengeKey(user.Id);
            var previousChallengeKey = await cache.GetStringAsync(userChallengeKey, ct);
            if (!string.IsNullOrWhiteSpace(previousChallengeKey))
            {
                await cache.RemoveAsync(previousChallengeKey, ct);
            }

            var challenge = new PasswordRecoveryChallenge(
                user.Id,
                PasswordRecoveryCodeHash(challengeId, verificationCode, cfg),
                PasswordHashMarker(user.PasswordHash),
                expiresAt,
                0);

            await cache.SetStringAsync(
                challengeKey,
                JsonSerializer.Serialize(challenge),
                new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAt },
                ct);
            await cache.SetStringAsync(
                userChallengeKey,
                challengeKey,
                new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAt },
                ct);
            SetPasswordRecoveryCookie(http, PasswordRecoveryChallengeCookie, challengeId, expiresAt);

            var delivered = await SendPasswordRecoveryTelegramAsync(
                botToken,
                user,
                verificationCode,
                PasswordRecoveryCodeLifetimeMinutes(cfg),
                loggerFactory.CreateLogger("TaskForge.PasswordRecovery"),
                ct);
            if (!delivered)
            {
                await cache.RemoveAsync(challengeKey, ct);
                await cache.RemoveAsync(userChallengeKey, ct);
                ClearPasswordRecoveryCookies(http);
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    available = false,
                    message = "Не удалось отправить код в Telegram. Попробуйте позже.",
                    code = "RECOVERY_DELIVERY_FAILED"
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                available = true,
                message = "Код восстановления отправлен в привязанный Telegram.",
                expiresInSeconds = (int)lifetime.TotalSeconds,
                botUsername = TelegramBotUsername(cfg)
            });
        });

        app.MapPost("/api/auth/password-recovery/verify", async (
            PasswordRecoveryVerifyRequest request,
            HttpContext http,
            IDistributedCache cache,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (CheckAuthRateLimit(http, "password-recovery-verify") is { } limited) return limited;

            var challengeId = ReadCookie(http, PasswordRecoveryChallengeCookie);
            var verificationCode = NormalizePasswordRecoveryCode(request.VerificationCode);
            if (string.IsNullOrWhiteSpace(challengeId) || verificationCode.Length != 8 || verificationCode.Any(ch => !char.IsDigit(ch)))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Код восстановления указан неверно или запрос уже истёк.",
                    code = "RECOVERY_CHALLENGE_INVALID"
                });
            }

            var challengeKey = PasswordRecoveryChallengeKey(challengeId);
            var raw = await cache.GetStringAsync(challengeKey, ct);
            var challenge = DeserializePasswordRecoveryChallenge(raw);
            if (challenge == null || challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                await cache.RemoveAsync(challengeKey, ct);
                ClearPasswordRecoveryCookies(http);
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Код восстановления истёк. Запросите новый код.",
                    code = "RECOVERY_CHALLENGE_EXPIRED"
                });
            }

            var expectedHash = PasswordRecoveryCodeHash(challengeId, verificationCode, cfg);
            if (!FixedTimeEqualsHex(expectedHash, challenge.CodeHash))
            {
                var attempts = challenge.FailedAttempts + 1;
                var maxAttempts = PasswordRecoveryCodeAttemptLimit(cfg);
                if (attempts >= maxAttempts)
                {
                    await cache.RemoveAsync(challengeKey, ct);
                    await cache.RemoveAsync(PasswordRecoveryUserChallengeKey(challenge.UserId), ct);
                    ClearPasswordRecoveryCookies(http);
                    return Microsoft.AspNetCore.Http.Results.Json(new
                    {
                        message = "Слишком много неверных попыток. Запросите новый код.",
                        code = "RECOVERY_CODE_ATTEMPTS_EXCEEDED"
                    }, statusCode: StatusCodes.Status429TooManyRequests);
                }

                var updated = challenge with { FailedAttempts = attempts };
                await cache.SetStringAsync(
                    challengeKey,
                    JsonSerializer.Serialize(updated),
                    new DistributedCacheEntryOptions { AbsoluteExpiration = challenge.ExpiresAtUtc },
                    ct);
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Неверный код восстановления.",
                    code = "RECOVERY_CODE_INVALID",
                    attemptsRemaining = maxAttempts - attempts
                });
            }

            var resetToken = NewOpaquePasswordRecoveryToken();
            var resetLifetime = TimeSpan.FromMinutes(PasswordRecoveryResetLifetimeMinutes(cfg));
            var resetExpiresAt = DateTimeOffset.UtcNow.Add(resetLifetime);
            var grant = new PasswordRecoveryGrant(challenge.UserId, challenge.PasswordHashMarker, resetExpiresAt);
            await cache.SetStringAsync(
                PasswordRecoveryGrantKey(resetToken),
                JsonSerializer.Serialize(grant),
                new DistributedCacheEntryOptions { AbsoluteExpiration = resetExpiresAt },
                ct);

            await cache.RemoveAsync(challengeKey, ct);
            await cache.RemoveAsync(PasswordRecoveryUserChallengeKey(challenge.UserId), ct);
            DeletePasswordRecoveryCookie(http, PasswordRecoveryChallengeCookie);
            SetPasswordRecoveryCookie(http, PasswordRecoveryResetCookie, resetToken, resetExpiresAt);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                verified = true,
                message = "Telegram подтверждён. Теперь задайте новый пароль.",
                expiresInSeconds = (int)resetLifetime.TotalSeconds
            });
        });

        app.MapPost("/api/auth/password-recovery/reset", async (
            PasswordRecoveryResetRequest request,
            HttpContext http,
            IdentityDbContext db,
            IDistributedCache cache,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (CheckAuthRateLimit(http, "password-recovery-reset") is { } limited) return limited;
            if (!IsValidPassword(request.NewPassword, out var passwordMessage))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = passwordMessage });
            }

            var resetToken = ReadCookie(http, PasswordRecoveryResetCookie);
            if (string.IsNullOrWhiteSpace(resetToken))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Подтверждение восстановления отсутствует или истекло.",
                    code = "RECOVERY_GRANT_INVALID"
                });
            }

            var grantKey = PasswordRecoveryGrantKey(resetToken);
            var raw = await cache.GetStringAsync(grantKey, ct);
            var grant = DeserializePasswordRecoveryGrant(raw);
            if (grant == null || grant.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                await cache.RemoveAsync(grantKey, ct);
                ClearPasswordRecoveryCookies(http);
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Время восстановления истекло. Начните заново.",
                    code = "RECOVERY_GRANT_EXPIRED"
                });
            }

            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == grant.UserId, ct);
            if (user == null || !string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
            {
                await cache.RemoveAsync(grantKey, ct);
                ClearPasswordRecoveryCookies(http);
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Аккаунт больше недоступен.",
                    code = "RECOVERY_ACCOUNT_UNAVAILABLE"
                });
            }

            if (!string.Equals(PasswordHashMarker(user.PasswordHash), grant.PasswordHashMarker, StringComparison.Ordinal))
            {
                await cache.RemoveAsync(grantKey, ct);
                ClearPasswordRecoveryCookies(http);
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Пароль уже был изменён. Войдите с новым паролем или начните восстановление заново.",
                    code = "RECOVERY_PASSWORD_ALREADY_CHANGED"
                });
            }

            if (VerifyPassword(request.NewPassword ?? string.Empty, user.PasswordSalt, user.PasswordHash))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new
                {
                    message = "Новый пароль должен отличаться от предыдущего."
                });
            }

            var salt = NewSalt();
            user.PasswordSalt = salt;
            user.PasswordHash = HashPassword(request.NewPassword ?? string.Empty, salt);
            await db.SaveChangesAsync(ct);

            await cache.RemoveAsync(grantKey, ct);
            ClearPasswordRecoveryCookies(http);
            ClearAuthCookies(http);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                completed = true,
                message = "Пароль изменён. Теперь можно войти в аккаунт.",
                login = UserLoginOrFallback(user)
            });
        });

        return app;
    }

    private static async Task<IdentityUser?> FindPasswordRecoveryUserAsync(IdentityDbContext db, string identity, CancellationToken ct)
    {
        if (identity.Contains('@'))
        {
            var email = NormalizeOptionalEmail(identity);
            return string.IsNullOrWhiteSpace(email)
                ? null
                : await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
        }

        var login = NormalizeLogin(identity);
        return string.IsNullOrWhiteSpace(login)
            ? null
            : await db.Users.FirstOrDefaultAsync(x => x.Login == login, ct);
    }

    private static IResult PasswordRecoveryUnavailable() => Microsoft.AspNetCore.Http.Results.Ok(new
    {
        available = false,
        message = "Извините, автоматическое восстановление невозможно: аккаунт не найден или к нему не привязан Telegram.",
        code = "RECOVERY_NOT_AVAILABLE"
    });

    private static async Task<bool> SendPasswordRecoveryTelegramAsync(
        string botToken,
        IdentityUser user,
        string verificationCode,
        int lifetimeMinutes,
        ILogger logger,
        CancellationToken ct)
    {
        if (user.TelegramChatId == null) return false;

        var text =
            "🔐 Восстановление аккаунта TaskForge\n\n" +
            $"Аккаунт: {DisplayName(user)}\n" +
            $"Логин: {UserLoginOrFallback(user)}\n" +
            $"Код восстановления: {verificationCode}\n\n" +
            $"Код действует {lifetimeMinutes} мин. Никому его не сообщайте.\n" +
            "Если вы не запрашивали восстановление, просто проигнорируйте это сообщение.";

        try
        {
            var endpoint = new Uri($"https://api.telegram.org/bot{botToken}/sendMessage");
            using var response = await PasswordRecoveryTelegramClient.PostAsJsonAsync(endpoint, new
            {
                chat_id = user.TelegramChatId.Value,
                text,
                disable_web_page_preview = true
            }, ct);
            if (response.IsSuccessStatusCode) return true;

            logger.LogWarning("Password recovery Telegram delivery failed with status {StatusCode}.", (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Password recovery Telegram delivery failed. Exception type: {ExceptionType}", ex.GetType().Name);
            return false;
        }
    }

    private static string PasswordRecoveryBotToken(IConfiguration cfg) => FirstNonEmpty(
        cfg["Telegram:BotToken"],
        cfg["SUPPORT_BOT_TOKEN"],
        cfg["TELEGRAM_BOT_TOKEN"]) ?? string.Empty;

    private static int PasswordRecoveryCodeLifetimeMinutes(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue("PasswordRecovery:CodeLifetimeMinutes", 10), 3, 30);

    private static int PasswordRecoveryResetLifetimeMinutes(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue("PasswordRecovery:ResetLifetimeMinutes", 10), 3, 30);

    private static int PasswordRecoveryCodeAttemptLimit(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue("PasswordRecovery:CodeAttemptLimit", 6), 3, 12);

    private static int PasswordRecoveryIssueLimit(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue("PasswordRecovery:IssueLimit", 3), 1, 10);

    private static int PasswordRecoveryIssueWindowMinutes(IConfiguration cfg)
        => Math.Clamp(cfg.GetValue("PasswordRecovery:IssueWindowMinutes", 60), 5, 1440);

    private static string NewOpaquePasswordRecoveryToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string NormalizePasswordRecoveryCode(string? value)
        => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string PasswordRecoveryChallengeKey(string challengeId)
        => $"identity:password-recovery:challenge:{HashOpaquePasswordRecoveryValue(challengeId)}";

    private static string PasswordRecoveryGrantKey(string resetToken)
        => $"identity:password-recovery:grant:{HashOpaquePasswordRecoveryValue(resetToken)}";

    private static string PasswordRecoveryUserChallengeKey(Guid userId)
        => $"identity:password-recovery:user:{userId:N}";

    private static string HashOpaquePasswordRecoveryValue(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string PasswordHashMarker(string passwordHash)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? string.Empty))).ToLowerInvariant();

    private static string PasswordRecoveryCodeHash(string challengeId, string verificationCode, IConfiguration cfg)
    {
        var key = cfg["PasswordRecovery:SigningKey"]
            ?? cfg["Jwt:Key"]
            ?? cfg["Jwt:SigningKey"]
            ?? Environment.GetEnvironmentVariable("JWT_SIGNING_KEY")
            ?? "taskforge-password-recovery-dev-key";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{challengeId}:{verificationCode}"))).ToLowerInvariant();
    }

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch
        {
            return false;
        }
    }

    private static PasswordRecoveryChallenge? DeserializePasswordRecoveryChallenge(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<PasswordRecoveryChallenge>(raw); }
        catch { return null; }
    }

    private static PasswordRecoveryGrant? DeserializePasswordRecoveryGrant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<PasswordRecoveryGrant>(raw); }
        catch { return null; }
    }

    private static void SetPasswordRecoveryCookie(HttpContext http, string name, string value, DateTimeOffset expiresAt)
    {
        var secure = string.Equals(http.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase) || http.Request.IsHttps;
        http.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Expires = expiresAt,
            Path = "/",
            IsEssential = true
        });
    }

    private static void DeletePasswordRecoveryCookie(HttpContext http, string name)
        => http.Response.Cookies.Delete(name, new CookieOptions { Path = "/" });

    private static void ClearPasswordRecoveryCookies(HttpContext http)
    {
        DeletePasswordRecoveryCookie(http, PasswordRecoveryChallengeCookie);
        DeletePasswordRecoveryCookie(http, PasswordRecoveryResetCookie);
    }

    private sealed record PasswordRecoveryChallenge(
        Guid UserId,
        string CodeHash,
        string PasswordHashMarker,
        DateTimeOffset ExpiresAtUtc,
        int FailedAttempts);

    private sealed record PasswordRecoveryGrant(
        Guid UserId,
        string PasswordHashMarker,
        DateTimeOffset ExpiresAtUtc);
}
