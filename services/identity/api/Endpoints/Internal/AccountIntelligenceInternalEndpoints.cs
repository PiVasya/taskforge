using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TaskForge.Identity.Api.Data;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private static WebApplication MapAccountIntelligenceInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-intelligence/identity-snapshot", async (
            IdentityDbContext db,
            IConfiguration cfg,
            int days = 365,
            CancellationToken ct = default) =>
        {
            days = Math.Clamp(days, 30, 1095);
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            const int loginSampleLimit = 250_000;
            var users = await db.Users.AsNoTracking()
                .Where(x => x.AccountStatus == "active")
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(ct);
            var blocks = await db.BlockedAccounts.AsNoTracking()
                .Where(x => !x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > DateTimeOffset.UtcNow)
                .ToDictionaryAsync(x => x.UserId, ct);
            var logs = await db.LoginLogs.AsNoTracking()
                .Where(x => x.LoginAt >= since)
                .OrderByDescending(x => x.LoginAt)
                .Take(loginSampleLimit)
                .Select(x => new { x.UserId, x.LoginAt, x.IpAddress, x.UserAgent, x.DeviceHash })
                .ToListAsync(ct);

            var correlationKey = cfg["AccountIntelligence:CorrelationHashKey"]
                ?? cfg["InternalApi:Key"]
                ?? cfg["Jwt:Key"]
                ?? cfg["Jwt:SigningKey"]
                ?? "taskforge-account-intelligence-dev-key";

            var byUser = logs.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.ToList());
            var items = users.Select(user =>
            {
                var userLogs = byUser.GetValueOrDefault(user.Id) ?? [];
                var extra = ReadPublicProfileExtra(user.AdditionalDataJson);
                return new
                {
                    userId = user.Id,
                    user.Login,
                    user.Email,
                    user.FirstName,
                    user.LastName,
                    user.PhoneNumber,
                    user.ProfilePictureUrl,
                    user.Role,
                    user.CreatedAt,
                    user.LastLoginAt,
                    user.TelegramChatId,
                    user.TelegramUsername,
                    user.TelegramLinkedAtUtc,
                    user.TelegramLinkCount,
                    accountStatus = user.AccountStatus,
                    blocked = blocks.ContainsKey(user.Id),
                    blockReason = blocks.GetValueOrDefault(user.Id)?.Reason,
                    location = extra.Location,
                    education = extra.Education,
                    github = extra.Github,
                    profileTelegram = extra.Telegram,
                    website = extra.Website,
                    loginCount = userLogs.Count,
                    loginDays = userLogs.Select(x => x.LoginAt.UtcDateTime.Date).Distinct().Count(),
                    lastLoginLogAt = userLogs.Count == 0 ? (DateTimeOffset?)null : userLogs.Max(x => x.LoginAt),
                    ipHashes = userLogs.Select(x => HashCorrelation(x.IpAddress, correlationKey)).Where(x => x != null).Distinct().Take(64).ToArray(),
                    deviceHashes = userLogs.Select(x => x.DeviceHash).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray(),
                    userAgentHashes = userLogs.Select(x => HashCorrelation(x.UserAgent, correlationKey)).Where(x => x != null).Distinct().Take(64).ToArray(),
                };
            }).ToList();

            return Results.Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                periodDays = days,
                loginSampleLimit,
                sampledLoginRows = logs.Count,
                items
            });
        });

        return app;
    }

    private static string? HashCorrelation(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    }
}
