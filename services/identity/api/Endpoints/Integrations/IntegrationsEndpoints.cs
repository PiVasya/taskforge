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
    private static WebApplication MapIntegrationsEndpoints(WebApplication app)
    {
        app.MapGet("/api/integrations/telegram/status", async (HttpContext http, IdentityDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");

            return Microsoft.AspNetCore.Http.Results.Ok(TelegramStatus(user, cfg));
        });

        app.MapPost("/api/integrations/telegram/code", async (HttpContext http, IdentityDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            if (user.TelegramChatId != null)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Telegram уже привязан к аккаунту." });
            }
            if (user.TelegramLinkCount >= TelegramMaxLinkCount(cfg))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Лимит привязок Telegram исчерпан." });
            }

            var oldCodes = await db.TelegramLinkCodes.Where(x => x.UserId == user.Id).ToListAsync(ct);
            if (oldCodes.Count > 0) db.TelegramLinkCodes.RemoveRange(oldCodes);

            var code = NewTelegramCode();
            var expires = DateTimeOffset.UtcNow.AddMinutes(TelegramCodeLifetimeMinutes(cfg));
            db.TelegramLinkCodes.Add(new TelegramLinkCode
            {
                UserId = user.Id,
                CodeHash = HashTelegramCode(code),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAtUtc = expires
            });
            await db.SaveChangesAsync(ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                code,
                expiresAtUtc = expires,
                expiresInSeconds = (int)Math.Max(0, (expires - DateTimeOffset.UtcNow).TotalSeconds),
                status = TelegramStatus(user, cfg)
            });
        });

        app.MapDelete("/api/integrations/telegram/unlink", async (HttpContext http, IdentityDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");

            user.TelegramChatId = null;
            user.TelegramUsername = null;
            user.TelegramLinkedAtUtc = null;
            await db.TelegramLinkCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(TelegramStatus(user, cfg));
        });

        app.MapPost("/api/internal/integrations/telegram/confirm", async (TelegramConfirmRequest req, IdentityDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var normalized = NormalizeTelegramCode(req.Code);
            if (string.IsNullOrWhiteSpace(normalized) || req.ChatId == 0)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { ok = false, message = "Код привязки не указан." });
            }

            var codeHash = HashTelegramCode(normalized);
            var now = DateTimeOffset.UtcNow;
            var linkCode = await db.TelegramLinkCodes.FirstOrDefaultAsync(x => x.CodeHash == codeHash && x.UsedAtUtc == null, ct);
            if (linkCode == null || linkCode.ExpiresAtUtc < now)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { ok = false, message = "Код не найден или истёк." });
            }

            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == linkCode.UserId, ct);
            if (user == null)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { ok = false, message = "Пользователь не найден." });
            }
            if (user.TelegramChatId != null)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { ok = false, message = "Telegram уже привязан к аккаунту." });
            }
            if (user.TelegramLinkCount >= TelegramMaxLinkCount(cfg))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { ok = false, message = "Лимит привязок Telegram исчерпан." });
            }

            var sameChatUsed = await db.Users.AsNoTracking().AnyAsync(x => x.TelegramChatId == req.ChatId && x.Id != user.Id, ct);
            if (sameChatUsed)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { ok = false, message = "Этот Telegram уже привязан к другому аккаунту." });
            }

            user.TelegramChatId = req.ChatId;
            user.TelegramUsername = NormalizeTelegramUsername(req.Username);
            user.TelegramLinkedAtUtc = now;
            user.TelegramLinkCount += 1;
            linkCode.UsedAtUtc = now;
            await db.TelegramLinkCodes.Where(x => x.UserId == user.Id && x.Id != linkCode.Id).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, message = "Telegram привязан. Теперь можно писать в поддержку прямо боту." });
        });

        app.MapGet("/api/internal/integrations/telegram/by-chat/{chatId:long}", async (long chatId, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.TelegramChatId == chatId, ct);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Telegram не привязан." });
            return Microsoft.AspNetCore.Http.Results.Ok(TelegramContact(user));
        });

        app.MapGet("/api/internal/integrations/telegram/users/{userId:guid}", async (Guid userId, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null || user.TelegramChatId == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Telegram не привязан." });
            return Microsoft.AspNetCore.Http.Results.Ok(TelegramContact(user));
        });

        return app;
    }

    private static object TelegramStatus(IdentityUser user, IConfiguration cfg) => new
    {
        linked = user.TelegramChatId != null,
        username = user.TelegramUsername,
        linkedAtUtc = user.TelegramLinkedAtUtc,
        linkCount = user.TelegramLinkCount,
        maxLinkCount = TelegramMaxLinkCount(cfg),
        botUsername = TelegramBotUsername(cfg)
    };

    private static object TelegramContact(IdentityUser user) => new
    {
        userId = user.Id,
        telegramChatId = user.TelegramChatId,
        telegramUsername = user.TelegramUsername,
        login = UserLoginOrFallback(user),
        email = user.Email,
        maskedEmail = MaskEmail(user.Email),
        firstName = user.FirstName,
        lastName = user.LastName,
        displayName = DisplayName(user)
    };

    private static int TelegramMaxLinkCount(IConfiguration cfg) => Math.Clamp(cfg.GetValue("Telegram:MaxLinkCount", 2), 1, 20);

    private static int TelegramCodeLifetimeMinutes(IConfiguration cfg) => Math.Clamp(cfg.GetValue("Telegram:CodeLifetimeMinutes", 10), 1, 120);

    private static string TelegramBotUsername(IConfiguration cfg)
    {
        var value = FirstNonEmpty(
            cfg["Telegram:BotUsername"],
            cfg["SUPPORT_BOT_USERNAME"],
            cfg["TELEGRAM_BOT_USERNAME"],
            "@taskforgeby_bot")!;
        return value.StartsWith('@') ? value : "@" + value;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(x => (x ?? string.Empty).Trim()).FirstOrDefault(x => x.Length > 0);

    private static string NewTelegramCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    private static string NormalizeTelegramCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static string HashTelegramCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeTelegramCode(code)));
        return Convert.ToHexString(bytes);
    }

    private static string? NormalizeTelegramUsername(string? value)
    {
        var username = (value ?? string.Empty).Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(username) ? null : username;
    }
}
