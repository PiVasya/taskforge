using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Identity.Api.Contracts;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private static WebApplication MapMessengerEndpoints(WebApplication app)
    {
        app.MapGet("/api/integrations/discord/status", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            return user == null ? Unauthorized("Сессия истекла. Войдите заново.") : Results.Ok(DiscordStatus(user, cfg));
        });

        app.MapPost("/api/integrations/discord/code", async (HttpContext http, IdentityDbContext db, IDistributedCache cache, IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            if (user.DiscordUserId != null) return Results.BadRequest(new { message = "Discord уже привязан к аккаунту." });
            if (!ValidDiscordSnowflake(cfg["Discord:BotUserId"]))
                return Results.Json(new { message = "Discord-бот пока не настроен администратором." }, statusCode: 503);
            var now = DateTimeOffset.UtcNow;
            var throttle = await TryConsumeTelegramThrottleAsync(cache, $"discord-code:{user.Id}",
                Math.Clamp(cfg.GetValue("Discord:CodeIssueLimit", 5), 1, 100),
                TimeSpan.FromMinutes(Math.Clamp(cfg.GetValue("Discord:CodeIssueWindowMinutes", 60), 1, 1440)), now, ct);
            if (!throttle.Allowed)
                return Results.Json(new { message = "Слишком много запросов кода.", retryAfterSeconds = throttle.RetryAfterSeconds }, statusCode: 429);

            var active = await db.DiscordLinkCodes.AsNoTracking()
                .Where(x => x.UserId == user.Id && x.UsedAtUtc == null && x.ExpiresAtUtc > now)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var cooldown = Math.Clamp(cfg.GetValue("Discord:CodeCooldownSeconds", 30), 0, 3600);
            if (active != null && active.CreatedAt.AddSeconds(cooldown) > now)
            {
                var wait = Math.Max(1, (int)Math.Ceiling((active.CreatedAt.AddSeconds(cooldown) - now).TotalSeconds));
                return Results.Json(new { message = "Код недавно создавался.", retryAfterSeconds = wait }, statusCode: 429);
            }

            await db.DiscordLinkCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
            var code = NewTelegramCode();
            var expires = now.AddMinutes(Math.Clamp(cfg.GetValue("Discord:CodeLifetimeMinutes", 10), 1, 120));
            db.DiscordLinkCodes.Add(new DiscordLinkCode
            {
                UserId = user.Id, CodeHash = HashTelegramCode(code), CreatedAt = now, ExpiresAtUtc = expires
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { code, expiresAtUtc = expires, status = DiscordStatus(user, cfg) });
        });

        app.MapDelete("/api/integrations/discord/unlink", async (HttpContext http, IdentityDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            ClearDiscordLink(user);
            await db.DiscordLinkCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(DiscordStatus(user, cfg));
        });

        app.MapGet("/api/integrations/messengers/status", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            return user == null ? Unauthorized("Сессия истекла. Войдите заново.") : Results.Ok(MessengerStatus(user));
        });

        app.MapPut("/api/integrations/messengers/primary", async (MessengerPrimaryRequest req, HttpContext http, IdentityDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            var messenger = (req.Messenger ?? "").Trim().ToLowerInvariant();
            if (messenger != "telegram" && messenger != "discord")
                return Results.BadRequest(new { message = "Выберите Telegram или Discord." });
            if (messenger == "telegram" && user.TelegramChatId == null || messenger == "discord" && user.DiscordUserId == null)
                return Results.BadRequest(new { message = "Сначала привяжите выбранный мессенджер." });
            user.PrimaryMessenger = messenger;
            await db.SaveChangesAsync(ct);
            return Results.Ok(MessengerStatus(user));
        });

        app.MapPost("/api/internal/integrations/discord/confirm", async (DiscordConfirmRequest req, IdentityDbContext db, IDistributedCache cache, IConfiguration cfg, CancellationToken ct) =>
        {
            var code = NormalizeTelegramCode(req.Code);
            var discordUserId = (req.DiscordUserId ?? "").Trim();
            if (code.Length != 8 || !ValidDiscordSnowflake(discordUserId))
                return Results.BadRequest(new { ok = false, message = "Некорректный код или пользователь Discord." });
            var now = DateTimeOffset.UtcNow;
            var throttle = await TryConsumeTelegramThrottleAsync(cache, $"discord-confirm:{HashTelegramCode(discordUserId)}",
                Math.Clamp(cfg.GetValue("Discord:ConfirmAttemptLimit", 8), 1, 100),
                TimeSpan.FromMinutes(Math.Clamp(cfg.GetValue("Discord:ConfirmAttemptWindowMinutes", 5), 1, 1440)), now, ct);
            if (!throttle.Allowed)
                return Results.Json(new { ok = false, message = "Слишком много попыток привязки.", retryAfterSeconds = throttle.RetryAfterSeconds }, statusCode: 429);

            var link = await db.DiscordLinkCodes.FirstOrDefaultAsync(x => x.CodeHash == HashTelegramCode(code) && x.UsedAtUtc == null, ct);
            if (link == null || link.ExpiresAtUtc <= now)
                return Results.BadRequest(new { ok = false, message = "Код не найден или истёк." });
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == link.UserId, ct);
            if (user == null || !string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { ok = false, message = "Аккаунт недоступен." });
            if (user.DiscordUserId != null)
                return Results.BadRequest(new { ok = false, message = "Discord уже привязан к аккаунту." });
            if (await db.Users.AsNoTracking().AnyAsync(x => x.DiscordUserId == discordUserId && x.Id != user.Id, ct))
                return Results.BadRequest(new { ok = false, message = "Этот Discord уже привязан к другому аккаунту." });

            user.DiscordUserId = discordUserId;
            user.DiscordName = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim()[..Math.Min(req.Name.Trim().Length, 160)];
            user.DiscordLinkedAtUtc = now;
            user.DiscordLinkCount++;
            user.PrimaryMessenger ??= user.TelegramChatId == null ? "discord" : "telegram";
            link.UsedAtUtc = now;
            await db.DiscordLinkCodes.Where(x => x.UserId == user.Id && x.Id != link.Id).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true, message = "Discord привязан к TaskForge.", primaryMessenger = EffectivePrimaryMessenger(user) });
        });

        app.MapGet("/api/internal/integrations/discord/by-subscriber", async (string? subscriberId, IdentityDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(subscriberId)) return Results.NotFound();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.DiscordUserId == subscriberId && x.AccountStatus == "active", ct);
            if (user == null) return Results.NotFound();
            return Results.Ok(new { userId = user.Id, discordUserId = user.DiscordUserId, primaryMessenger = EffectivePrimaryMessenger(user) });
        });

        app.MapGet("/api/internal/integrations/messengers/users/{userId:guid}", async (Guid userId, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId && x.AccountStatus == "active", ct);
            if (user == null) return Results.NotFound();
            return Results.Ok(new
            {
                userId, linked = user.TelegramChatId.HasValue || user.DiscordUserId != null,
                telegramChatId = user.TelegramChatId, discordUserId = user.DiscordUserId,
                primaryMessenger = EffectivePrimaryMessenger(user)
            });
        });

        return app;
    }

    internal static void ClearDiscordLink(IdentityUser user)
    {
        user.DiscordUserId = null;
        user.DiscordName = null;
        user.DiscordLinkedAtUtc = null;
        if (string.Equals(user.PrimaryMessenger, "discord", StringComparison.OrdinalIgnoreCase))
            user.PrimaryMessenger = user.TelegramChatId.HasValue ? "telegram" : null;
    }

    internal static string? EffectivePrimaryMessenger(IdentityUser user)
    {
        if (user.PrimaryMessenger == "discord" && user.DiscordUserId != null) return "discord";
        if (user.PrimaryMessenger == "telegram" && user.TelegramChatId.HasValue) return "telegram";
        if (user.TelegramChatId.HasValue) return "telegram";
        if (user.DiscordUserId != null) return "discord";
        return null;
    }

    private static object MessengerStatus(IdentityUser user) => new
    {
        telegramLinked = user.TelegramChatId.HasValue,
        discordLinked = user.DiscordUserId != null,
        linked = user.TelegramChatId.HasValue || user.DiscordUserId != null,
        primaryMessenger = EffectivePrimaryMessenger(user)
    };

    private static bool ValidDiscordSnowflake(string? id) =>
        id is { Length: >= 16 and <= 20 } && id.All(c => c is >= '0' and <= '9') && ulong.TryParse(id, out var number) && number > 0;

    private static bool ValidDiscordInvite(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("discord.gg", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase))
        && (uri.AbsolutePath.StartsWith("/invite/", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("discord.gg", StringComparison.OrdinalIgnoreCase));

    private static object DiscordStatus(IdentityUser user, IConfiguration cfg) => new
    {
        linked = user.DiscordUserId != null,
        name = user.DiscordName,
        linkedAtUtc = user.DiscordLinkedAtUtc,
        linkCount = user.DiscordLinkCount,
        botUrl = ValidDiscordSnowflake(cfg["Discord:BotUserId"]) ? "https://discord.com/users/" + cfg["Discord:BotUserId"] : null,
        serverInvite = ValidDiscordInvite(cfg["Discord:SupportServerInvite"]) ? cfg["Discord:SupportServerInvite"] : null,
        primaryMessenger = EffectivePrimaryMessenger(user)
    };
}
