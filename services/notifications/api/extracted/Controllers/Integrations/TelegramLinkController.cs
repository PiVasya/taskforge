using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Integrations;

/// <summary>
/// Привязка Telegram через одноразовый код.
/// Код генерируется на сайте, пользователь отправляет его боту, бот подтверждает через внутренний API.
/// </summary>
[ApiController]
[Route("api/integrations/telegram")]
public sealed class TelegramLinkController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _current;
    private readonly IConfiguration _config;

    public TelegramLinkController(ApplicationDbContext db, ICurrentUserService current, IConfiguration config)
    {
        _db = db;
        _current = current;
        _config = config;
    }

    public sealed record TelegramLinkStatusDto(
        bool Linked,
        string? Username,
        int LinkCount,
        string BotUsername
    );

    public sealed record TelegramLinkCodeDto(
        string Code,
        DateTime ExpiresAtUtc,
        TelegramLinkStatusDto Status
    );

    public sealed record TelegramConfirmRequest(string Code, long ChatId, string? Username);

    public sealed record TelegramConfirmResponse(bool Ok, string Message);

    private string GetBotUsername() => _config["TELEGRAM_BOT_USERNAME"] ?? "@taskforgeby_bot";

    private static string GenerateCode()
    {
        // 8 символов, только цифры/буквы без двусмысленных (0/O, 1/I)
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = bytes.ToArray().Select(b => alphabet[b % alphabet.Length]).ToArray();
        // формат XXXX-XXXX
        return new string(chars[..4]) + "-" + new string(chars[4..]);
    }

    private static (byte[] salt, byte[] hash) HashCode(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var saltB64 = Convert.ToBase64String(salt);
        var input = Encoding.UTF8.GetBytes(code.Trim() + ":" + saltB64);
        var hash = SHA256.HashData(input);
        return (salt, hash);
    }

    private static byte[] HashCodeWithSalt(string code, byte[] salt)
    {
        var saltB64 = Convert.ToBase64String(salt);
        var input = Encoding.UTF8.GetBytes(code.Trim() + ":" + saltB64);
        return SHA256.HashData(input);
    }

    [HttpGet("status")]
    [Authorize]
    public async Task<ActionResult<TelegramLinkStatusDto>> Status(CancellationToken ct)
    {
        var uid = _current.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        return Ok(new TelegramLinkStatusDto(
            user.TelegramChatId != null,
            string.IsNullOrWhiteSpace(user.TelegramUsername) ? null : ("@" + user.TelegramUsername),
            user.TelegramLinkCount,
            GetBotUsername()
        ));
    }

    /// <summary>
    /// Сгенерировать одноразовый код для привязки.
    /// Лимит: максимум 2 успешные привязки на пользователя.
    /// </summary>
    [HttpPost("code")]
    [Authorize]
    public async Task<ActionResult<TelegramLinkCodeDto>> Generate(CancellationToken ct)
    {
        var uid = _current.GetUserId();

        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        if (user.TelegramChatId != null)
            return Conflict(new { message = "Telegram уже привязан. Сначала отвяжи." });

        if (user.TelegramLinkCount >= 2)
            return StatusCode(403, new { message = "Лимит привязок Telegram исчерпан (2/2)." });

        // чистим старые коды этого пользователя
        var now = DateTime.UtcNow;
        var old = await _db.TelegramLinkCodes
            .Where(x => x.UserId == uid && (x.UsedAtUtc != null || x.ExpiresAtUtc < now))
            .ToListAsync(ct);
        if (old.Count > 0) _db.TelegramLinkCodes.RemoveRange(old);

        var code = GenerateCode();
        var (salt, hash) = HashCode(code);
        var expires = now.AddMinutes(10);

        _db.TelegramLinkCodes.Add(new TelegramLinkCode
        {
            Id = Guid.NewGuid(),
            UserId = uid,
            Salt = salt,
            CodeHash = hash,
            ExpiresAtUtc = expires,
            CreatedAtUtc = now
        });
        await _db.SaveChangesAsync(ct);

        var status = new TelegramLinkStatusDto(false, null, user.TelegramLinkCount, GetBotUsername());
        return Ok(new TelegramLinkCodeDto(code, expires, status));
    }

    /// <summary>
    /// Отвязать Telegram.
    /// </summary>
    [HttpDelete("unlink")]
    [Authorize]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        var uid = _current.GetUserId();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        user.TelegramChatId = null;
        user.TelegramUsername = null;
        user.TelegramLinkedAtUtc = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Внутренний эндпоинт: бот подтверждает код.
    /// Защита: X-Internal-Key (API_INTERNAL_KEY).
    /// </summary>
    [HttpPost("confirm")]
    [AllowAnonymous]
    public async Task<ActionResult<TelegramConfirmResponse>> Confirm([FromBody] TelegramConfirmRequest req, CancellationToken ct)
    {
        var expectedKey = _config["API_INTERNAL_KEY"];
        var header = Request.Headers["X-Internal-Key"].ToString();
        if (string.IsNullOrEmpty(expectedKey) || header != expectedKey)
            return Unauthorized(new TelegramConfirmResponse(false, "unauthorized"));

        var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length < 4 || code.Length > 32)
            return BadRequest(new TelegramConfirmResponse(false, "Неверный код."));

        var now = DateTime.UtcNow;
        var candidates = await _db.TelegramLinkCodes
            .Include(x => x.User)
            .Where(x => x.UsedAtUtc == null && x.ExpiresAtUtc >= now)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(200)
            .ToListAsync(ct);

        TelegramLinkCode? match = null;
        foreach (var c in candidates)
        {
            var h = HashCodeWithSalt(code, c.Salt);
            if (h.SequenceEqual(c.CodeHash))
            {
                match = c;
                break;
            }
        }

        if (match is null || match.User is null)
            return NotFound(new TelegramConfirmResponse(false, "Код не найден или устарел. Сгенерируй новый на сайте."));

        var user = match.User;

        if (user.TelegramChatId != null)
            return Conflict(new TelegramConfirmResponse(false, "Telegram уже привязан к этому аккаунту. Сначала отвяжи на сайте."));

        if (user.TelegramLinkCount >= 2)
            return StatusCode(403, new TelegramConfirmResponse(false, "Лимит привязок Telegram исчерпан (2/2)."));

        // не даём одному chatId быть привязанным к нескольким аккаунтам
        var already = await _db.Users.AsNoTracking().AnyAsync(u => u.TelegramChatId == req.ChatId, ct);
        if (already)
            return Conflict(new TelegramConfirmResponse(false, "Этот Telegram уже привязан к другому аккаунту."));

        user.TelegramChatId = req.ChatId;
        user.TelegramUsername = string.IsNullOrWhiteSpace(req.Username) ? null : req.Username.Trim().TrimStart('@');
        user.TelegramLinkedAtUtc = now;
        user.TelegramLinkCount += 1;
        user.UpdatedAt = now;

        match.UsedAtUtc = now;
        await _db.SaveChangesAsync(ct);

        return Ok(new TelegramConfirmResponse(true, "Telegram привязан. Можно вернуться на сайт."));
    }
}
