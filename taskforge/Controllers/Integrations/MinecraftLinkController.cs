using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Integrations;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Integrations;

/// <summary>
/// Привязка Minecraft (серверная часть на стороне TaskForge).
/// Плагина на сервере пока нет — но мы:
/// - генерируем одноразовый код,
/// - логируем код в API логах,
/// - (опционально) пытаемся отправить nick+code на Minecraft-сервер по URL из .env.
/// </summary>
[ApiController]
[Route("api/integrations/minecraft")]
public sealed class MinecraftLinkController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _current;
    private readonly IMinecraftServerNotifier _notifier;
    private readonly ILogger<MinecraftLinkController> _log;

    public MinecraftLinkController(ApplicationDbContext db, ICurrentUserService current, IMinecraftServerNotifier notifier, ILogger<MinecraftLinkController> log)
    {
        _db = db;
        _current = current;
        _notifier = notifier;
        _log = log;
    }

    public sealed record MinecraftStatusDto(
        bool Linked,
        string? Nick,
        string? Uuid,
        int LinkCount
    );

    public sealed record MinecraftRequestDto(string Nick);
    public sealed record MinecraftCodeDto(string Code, DateTime ExpiresAtUtc, MinecraftStatusDto Status);
    public sealed record MinecraftConfirmDto(string Code);

    private static readonly Regex NickRx = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    private static string GenerateCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = bytes.ToArray().Select(b => alphabet[b % alphabet.Length]).ToArray();
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
    public async Task<ActionResult<MinecraftStatusDto>> Status(CancellationToken ct)
    {
        var uid = _current.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        return Ok(new MinecraftStatusDto(
            !string.IsNullOrWhiteSpace(user.MinecraftNick),
            user.MinecraftNick,
            user.MinecraftUuid,
            user.MinecraftLinkCount
        ));
    }

    /// <summary>
    /// Запросить привязку: генерирует код и (опционально) отправляет nick+code на Minecraft-сервер.
    /// Лимит: максимум 2 успешные привязки.
    /// </summary>
    [HttpPost("request")]
    [Authorize]
    public async Task<ActionResult<MinecraftCodeDto>> Request([FromBody] MinecraftRequestDto req, CancellationToken ct)
    {
        var uid = _current.GetUserId();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        if (user.MinecraftLinkCount >= 2)
            return StatusCode(403, new { message = "Лимит привязок Minecraft исчерпан (2/2)." });

        var nick = (req.Nick ?? string.Empty).Trim();
        if (!NickRx.IsMatch(nick))
            return BadRequest(new { message = "Неверный ник. Разрешены A-Z, 0-9, _ (3..16 символов)." });

        // если уже привязан — требуем сначала отвязать
        if (!string.IsNullOrWhiteSpace(user.MinecraftNick))
            return Conflict(new { message = "Minecraft уже привязан. Сначала отвяжи." });

        var now = DateTime.UtcNow;

        // чистим старые коды этого пользователя
        var old = await _db.MinecraftLinkCodes
            .Where(x => x.UserId == uid && (x.UsedAtUtc != null || x.ExpiresAtUtc < now))
            .ToListAsync(ct);
        if (old.Count > 0) _db.MinecraftLinkCodes.RemoveRange(old);

        var code = GenerateCode();
        var (salt, hash) = HashCode(code);
        var expires = now.AddMinutes(10);

        _db.MinecraftLinkCodes.Add(new MinecraftLinkCode
        {
            Id = Guid.NewGuid(),
            UserId = uid,
            Nick = nick,
            Salt = salt,
            CodeHash = hash,
            ExpiresAtUtc = expires,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);

        // ВАЖНО: логируем код в API логах (как просил)
        _log.LogInformation("Minecraft link code generated: user={UserId} nick={Nick} code={Code} exp={Exp}", uid, nick, code, expires);

        // Пытаемся доставить на Minecraft-сервер (если настроено)
        var (ok, msg) = await _notifier.SendLinkCodeAsync(nick, code, ct);
        _log.LogInformation("Minecraft link delivery attempt: user={UserId} nick={Nick} ok={Ok} msg={Msg}", uid, nick, ok, msg);

        var status = new MinecraftStatusDto(false, null, null, user.MinecraftLinkCount);
        return Ok(new MinecraftCodeDto(code, expires, status));
    }

    /// <summary>
    /// Подтвердить привязку: пользователь вводит код на сайте.
    /// </summary>
    [HttpPost("confirm")]
    [Authorize]
    public async Task<IActionResult> Confirm([FromBody] MinecraftConfirmDto req, CancellationToken ct)
    {
        var uid = _current.GetUserId();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        if (user.MinecraftLinkCount >= 2)
            return StatusCode(403, new { message = "Лимит привязок Minecraft исчерпан (2/2)." });

        if (!string.IsNullOrWhiteSpace(user.MinecraftNick))
            return Conflict(new { message = "Minecraft уже привязан. Сначала отвяжи." });

        var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length < 4 || code.Length > 32)
            return BadRequest(new { message = "Неверный код." });

        var now = DateTime.UtcNow;
        var candidates = await _db.MinecraftLinkCodes
            .Where(x => x.UserId == uid && x.UsedAtUtc == null && x.ExpiresAtUtc >= now)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        MinecraftLinkCode? match = null;
        foreach (var c in candidates)
        {
            var h = HashCodeWithSalt(code, c.Salt);
            if (h.SequenceEqual(c.CodeHash))
            {
                match = c;
                break;
            }
        }

        if (match is null)
            return NotFound(new { message = "Код не найден или устарел. Сгенерируй новый." });

        // подтверждаем
        match.UsedAtUtc = now;
        user.MinecraftNick = match.Nick;
        user.MinecraftLinkedAtUtc = now;
        user.MinecraftLinkCount += 1;
        user.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Minecraft linked: user={UserId} nick={Nick} count={Count}", uid, user.MinecraftNick, user.MinecraftLinkCount);
        return Ok(new MinecraftStatusDto(true, user.MinecraftNick, user.MinecraftUuid, user.MinecraftLinkCount));
    }

    [HttpDelete("unlink")]
    [Authorize]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        var uid = _current.GetUserId();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        user.MinecraftNick = null;
        user.MinecraftUuid = null;
        user.MinecraftLinkedAtUtc = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
