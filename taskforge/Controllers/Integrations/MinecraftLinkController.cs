using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Integrations;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Integrations;

/// <summary>
/// Minecraft-интеграция (пока без плагина, но контракт уже готов).
///
/// 1) Привязка:
///    - TaskForge генерит одноразовый код
///    - (опционально) пытается отправить nick+code на Minecraft-сервер по URL из .env
///    - игрок вводит код на сайте → подтверждаем привязку
///
/// 2) "Недельный вход":
///    - если игрок зашёл хоть раз в неделю → считается 1 раз
///    - за каждую такую неделю считается "стоимость" (по умолчанию 70 рейтинга)
///    - если рейтинга не хватает, игрок считается задебафанным
///
/// Важно: штраф хранится в БД (MinecraftEconomySettings), чтобы ты мог менять число.
/// </summary>
[ApiController]
[Route("api/integrations/minecraft")]
public sealed class MinecraftLinkController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _current;
    private readonly IMinecraftServerNotifier _notifier;
    private readonly ILogger<MinecraftLinkController> _log;
    private readonly IConfiguration _cfg;
    private readonly IFeatureRoleService _featureRoles;

    public MinecraftLinkController(
        ApplicationDbContext db,
        ICurrentUserService current,
        IMinecraftServerNotifier notifier,
        ILogger<MinecraftLinkController> log,
        IConfiguration cfg,
        IFeatureRoleService featureRoles)
    {
        _db = db;
        _current = current;
        _notifier = notifier;
        _log = log;
        _cfg = cfg;
        _featureRoles = featureRoles;
    }

    // ========= DTO =========

    public sealed record MinecraftStatusDto(
        bool Linked,
        string? Nick,
        string? Uuid,
        int LinkCount,
        int WeeklyPenaltyCurrent,
        int PenaltyTotal,
        int Score,
        int EffectiveScore,
        bool Debuffed
    );

    public sealed record MinecraftRequestDto(string Nick);
    public sealed record MinecraftDeliveryDto(bool Attempted, bool Delivered, string Message);
    public sealed record MinecraftCodeDto(string Code, DateTime ExpiresAtUtc, MinecraftStatusDto Status, MinecraftDeliveryDto Delivery);
    public sealed record MinecraftConfirmDto(string Code);

    public sealed record MinecraftEconomyDto(int WeeklyPenalty);
    public sealed record MinecraftEconomyUpdateDto(int WeeklyPenalty);

    public sealed record MinecraftJoinEventDto(string Nick, string? Uuid);

    public sealed record MinecraftPlayerStatusDto(
        bool Linked,
        string? Nick,
        string? Uuid,
        int LinkCount,
        int Score,
        int WeeklyPenaltyCurrent,
        int PenaltyTotal,
        int EffectiveScore,
        bool ChargedThisWeek,
        bool Debuffed
    );

    private static readonly Regex NickRx = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    // ========= helpers =========

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

    private async Task<MinecraftEconomySettings> GetOrCreateEconomySettingsAsync(CancellationToken ct)
    {
        // В БД должна быть ровно 1 строка. Если её ещё нет (или БД новая) — создадим.
        var existing = await _db.MinecraftEconomySettings.FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        // По умолчанию берём из .env (если задано), иначе 70.
        var defaultPenalty = _cfg.GetValue<int?>("MINECRAFT_WEEKLY_PENALTY") ?? 70;

        var created = new MinecraftEconomySettings
        {
            Id = Guid.NewGuid(),
            WeeklyPenalty = defaultPenalty,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.MinecraftEconomySettings.Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    private static DateTime GetWeekStartUtc(DateTime utcNow)
    {
        // Неделя начинается в ПОНЕДЕЛЬНИК (UTC), чтобы у всех было одинаково.
        // DayOfWeek: Sunday=0, Monday=1...
        var day = (int)utcNow.DayOfWeek;
        var diffToMonday = day == 0 ? 6 : day - 1; // Sunday -> 6
        var monday = utcNow.Date.AddDays(-diffToMonday);
        return monday; // date
    }

    private string? GetPluginKey()
        => _cfg["MINECRAFT_PLUGIN_KEY"] ?? _cfg["MINECRAFT_SERVER_KEY"]; // один общий секрет, если не хочешь плодить

    private bool IsValidPluginCall()
    {
        var key = GetPluginKey();
        if (string.IsNullOrWhiteSpace(key)) return false;

        if (!Request.Headers.TryGetValue("X-Minecraft-Key", out var got)) return false;
        return string.Equals(got.ToString(), key, StringComparison.Ordinal);
    }

    private async Task<int> GetWeeklyPenaltyAsync(CancellationToken ct)
    {
        // 1) пробуем из БД
        var row = await _db.MinecraftEconomySettings.AsNoTracking().OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct);
        if (row != null && row.WeeklyPenalty > 0) return row.WeeklyPenalty;

        // 2) fallback из env
        if (int.TryParse(_cfg["MINECRAFT_WEEKLY_PENALTY"], out var p) && p > 0) return p;

        return 70;
    }

    private async Task<int> GetUserScoreAsync(Guid userId, CancellationToken ct)
    {
        // Считаем общий рейтинг как в лидерборде: сумма рейтингов за УНИКАЛЬНЫЕ решённые задания.
        // Для одного пользователя можно сделать просто 3 запроса + дедуп в памяти.

        var dict = new Dictionary<Guid, int>();

        var codeRows = await _db.UserTaskSolutions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.PassedAllTests)
            .Include(s => s.TaskAssignment)
            .Select(s => new { s.TaskAssignmentId, Rating = s.TaskAssignment.Rating })
            .ToListAsync(ct);

        foreach (var r in codeRows)
            dict[r.TaskAssignmentId] = dict.TryGetValue(r.TaskAssignmentId, out var cur) ? Math.Max(cur, r.Rating) : r.Rating;

        var testRows = await _db.UserTaskTestAttempts
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.Passed)
            .Include(t => t.TaskAssignment)
            .Select(t => new { t.TaskAssignmentId, Rating = t.TaskAssignment.Rating })
            .ToListAsync(ct);

        foreach (var r in testRows)
            dict[r.TaskAssignmentId] = dict.TryGetValue(r.TaskAssignmentId, out var cur) ? Math.Max(cur, r.Rating) : r.Rating;

        var imageRows = await _db.UserImageTaskSolutions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.Passed == true && s.IsTrial == false)
            .Include(s => s.TaskAssignment)
            .Select(s => new { s.TaskAssignmentId, Rating = s.TaskAssignment.Rating })
            .ToListAsync(ct);

        foreach (var r in imageRows)
            dict[r.TaskAssignmentId] = dict.TryGetValue(r.TaskAssignmentId, out var cur) ? Math.Max(cur, r.Rating) : r.Rating;

        return dict.Values.Sum();
    }

    // ========= API: link =========

    [HttpGet("status")]
    [Authorize]
    public async Task<ActionResult<MinecraftStatusDto>> Status(CancellationToken ct)
    {
        var uid = _current.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (user is null) return NotFound();

        if (string.IsNullOrWhiteSpace(user.MinecraftNick) || string.IsNullOrWhiteSpace(user.MinecraftUuid))
        {
            return Ok(new MinecraftStatusDto(
                false,
                user.MinecraftNick,
                user.MinecraftUuid,
                user.MinecraftLinkCount,
                WeeklyPenaltyCurrent: 0,
                PenaltyTotal: 0,
                Score: 0,
                EffectiveScore: 0,
                Debuffed: false
            ));
        }

        var settings = await GetOrCreateEconomySettingsAsync(ct);
        var penaltyTotal = await _db.MinecraftWeeklyJoins
            .AsNoTracking()
            .Where(x => x.UserId == uid)
            .SumAsync(x => (int?)x.PenaltyApplied, ct) ?? 0;

        var score = await GetUserScoreAsync(uid, ct);
        var effective = score - penaltyTotal;

        return Ok(new MinecraftStatusDto(
            true,
            user.MinecraftNick,
            user.MinecraftUuid,
            user.MinecraftLinkCount,
            WeeklyPenaltyCurrent: settings.WeeklyPenalty,
            PenaltyTotal: penaltyTotal,
            Score: score,
            EffectiveScore: effective,
            Debuffed: effective < 0
        ));
    }

    /// <summary>
    /// Запросить привязку: генерирует код и (опционально) отправляет nick+code на Minecraft-сервер.
    /// Лимит: максимум 2 успешные привязки.
    /// </summary>
    [HttpPost("request")]
    [Authorize]
    // ВАЖНО: метод нельзя называть "Request", т.к. у ControllerBase уже есть свойство Request.
    // Иначе внутри контроллера обращения вида Request.Headers начинают конфликтовать с этим методом.
    public async Task<ActionResult<MinecraftCodeDto>> RequestLink([FromBody] MinecraftRequestDto req, CancellationToken ct)
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

        var attempted = !string.IsNullOrWhiteSpace(_cfg["MINECRAFT_WEBHOOK_BASE_URL"]) || !string.IsNullOrWhiteSpace(_cfg["MINECRAFT_SERVER_URL"]);

        var delivery = new MinecraftDeliveryDto(
            Attempted: attempted,
            Delivered: ok,
            Message: msg
        );

        var status = new MinecraftStatusDto(
            Linked: false,
            Nick: null,
            Uuid: null,
            LinkCount: user.MinecraftLinkCount,
            WeeklyPenaltyCurrent: 0,
            PenaltyTotal: 0,
            Score: 0,
            EffectiveScore: 0,
            Debuffed: false
        );
        return Ok(new MinecraftCodeDto(code, expires, status, delivery));
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
        await _featureRoles.AssignRoleAsync(uid, FeatureRoles.Minecraft, uid, ct);

        _log.LogInformation("Minecraft linked: user={UserId} nick={Nick} count={Count}", uid, user.MinecraftNick, user.MinecraftLinkCount);

        var penaltyCurrent = await GetWeeklyPenaltyAsync(ct);
        var penaltyTotal = await _db.MinecraftWeeklyJoins
            .AsNoTracking()
            .Where(x => x.UserId == uid)
            .SumAsync(x => (int?)x.PenaltyApplied, ct) ?? 0;
        var score = await GetUserScoreAsync(uid, ct);
        var effective = score - penaltyTotal;

        return Ok(new MinecraftStatusDto(
            Linked: true,
            Nick: user.MinecraftNick,
            Uuid: user.MinecraftUuid,
            LinkCount: user.MinecraftLinkCount,
            WeeklyPenaltyCurrent: penaltyCurrent,
            PenaltyTotal: penaltyTotal,
            Score: score,
            EffectiveScore: effective,
            Debuffed: effective < 0
        ));
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
        await _featureRoles.RemoveRoleAsync(uid, FeatureRoles.Minecraft, ct);
        return NoContent();
    }

    // ========= API: economy settings (admin) =========

    [HttpGet("economy")]
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Editor)]
    public async Task<ActionResult<MinecraftEconomyDto>> GetEconomy(CancellationToken ct)
    {
        var p = await GetWeeklyPenaltyAsync(ct);
        return Ok(new MinecraftEconomyDto(p));
    }

    [HttpPost("economy")]
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Editor)]
    public async Task<ActionResult<MinecraftEconomyDto>> UpdateEconomy([FromBody] MinecraftEconomyUpdateDto dto, CancellationToken ct)
    {
        if (dto.WeeklyPenalty <= 0 || dto.WeeklyPenalty > 100000)
            return BadRequest(new { message = "WeeklyPenalty должен быть > 0" });

        var now = DateTime.UtcNow;
        _db.MinecraftEconomySettings.Add(new MinecraftEconomySettings
        {
            Id = Guid.NewGuid(),
            WeeklyPenalty = dto.WeeklyPenalty,
            UpdatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Minecraft economy updated: weeklyPenalty={Penalty}", dto.WeeklyPenalty);
        return Ok(new MinecraftEconomyDto(dto.WeeklyPenalty));
    }

    // ========= API: plugin events =========

    /// <summary>
    /// Сообщение от плагина: игрок зашёл на сервер.
    ///
    /// - Один раз в неделю фиксируем вход (MinecraftWeeklyJoin)
    /// - Возвращаем статус: задебафан или нет
    ///
    /// Защита: X-Minecraft-Key: (MINECRAFT_PLUGIN_KEY или MINECRAFT_SERVER_KEY)
    /// </summary>
    [HttpPost("events/join")]
    [AllowAnonymous]
    public async Task<ActionResult<MinecraftPlayerStatusDto>> Join([FromBody] MinecraftJoinEventDto dto, CancellationToken ct)
    {
        if (!IsValidPluginCall())
            return Unauthorized(new { message = "bad key" });

        var nick = (dto.Nick ?? string.Empty).Trim();
        if (!NickRx.IsMatch(nick))
            return BadRequest(new { message = "bad nick" });

        var uuid = (dto.Uuid ?? string.Empty).Trim();

        // Находим пользователя по UUID (когда будет) или по нику.
        User? user = null;
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.MinecraftUuid != null && u.MinecraftUuid == uuid, ct);
        }

        if (user == null)
        {
            var nickLower = nick.ToLowerInvariant();
            user = await _db.Users.FirstOrDefaultAsync(u => u.MinecraftNick != null && u.MinecraftNick.ToLower() == nickLower, ct);
        }

        if (user == null)
            return Ok(new MinecraftPlayerStatusDto(
                Linked: false,
                Nick: nick,
                Uuid: string.IsNullOrWhiteSpace(uuid) ? null : uuid,
                LinkCount: 0,
                Score: 0,
                WeeklyPenaltyCurrent: await GetWeeklyPenaltyAsync(ct),
                PenaltyTotal: 0,
                EffectiveScore: 0,
                ChargedThisWeek: false,
                Debuffed: true
            ));

        // если пришёл UUID — сохраним (это удобно, если ник сменится)
        if (!string.IsNullOrWhiteSpace(uuid) && user.MinecraftUuid != uuid)
        {
            user.MinecraftUuid = uuid;
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        var now = DateTime.UtcNow;
        var weekStart = GetWeekStartUtc(now);

        var existsThisWeek = await _db.MinecraftWeeklyJoins
            .AsNoTracking()
            .AnyAsync(x => x.UserId == user.Id && x.WeekStartUtc == weekStart, ct);

        var chargedThisWeek = false;

        // Текущий штраф за неделю (может меняться) — если создаём запись, фиксируем именно это значение.
        var penalty = await GetWeeklyPenaltyAsync(ct);

        // Суммарные списания по неделям (храним именно пенальти, а не количество недель).
        var penaltyTotal = await _db.MinecraftWeeklyJoins
            .AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .SumAsync(x => (int?)x.PenaltyApplied, ct) ?? 0;

        if (!existsThisWeek)
        {
            _db.MinecraftWeeklyJoins.Add(new MinecraftWeeklyJoin
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                WeekStartUtc = weekStart,
                CreatedAtUtc = now,
                PenaltyApplied = penalty
            });
            await _db.SaveChangesAsync(ct);
            penaltyTotal += penalty;
            chargedThisWeek = true;
        }
        var score = await GetUserScoreAsync(user.Id, ct);

        var effectiveScore = score - penaltyTotal;
        var debuffed = effectiveScore < 0;

        _log.LogInformation(
            "MC join: nick={Nick} user={UserId} week={Week} charged={Charged} score={Score} penaltyWeek={PenaltyWeek} penaltyTotal={PenaltyTotal} effective={Effective} debuffed={Debuffed}",
            nick, user.Id, weekStart.ToString("yyyy-MM-dd"), chargedThisWeek, score, penalty, penaltyTotal, effectiveScore, debuffed);

        return Ok(new MinecraftPlayerStatusDto(
            Linked: true,
            Nick: user.MinecraftNick,
            Uuid: user.MinecraftUuid,
            LinkCount: user.MinecraftLinkCount,
            Score: score,
            WeeklyPenaltyCurrent: penalty,
            PenaltyTotal: penaltyTotal,
            EffectiveScore: effectiveScore,
            ChargedThisWeek: chargedThisWeek,
            Debuffed: debuffed
        ));
    }

    /// <summary>
    /// Получить текущий статус игрока (без списания за неделю).
    /// Плагин может вызывать на смерть/периодически.
    /// </summary>
    [HttpGet("player-status")]
    [AllowAnonymous]
    public async Task<ActionResult<MinecraftPlayerStatusDto>> PlayerStatus([FromQuery] string? nick, [FromQuery] string? uuid, CancellationToken ct)
    {
        if (!IsValidPluginCall())
            return Unauthorized(new { message = "bad key" });

        var n = (nick ?? string.Empty).Trim();
        var u = (uuid ?? string.Empty).Trim();

        User? user = null;
        if (!string.IsNullOrWhiteSpace(u))
            user = await _db.Users.FirstOrDefaultAsync(x => x.MinecraftUuid != null && x.MinecraftUuid == u, ct);

        if (user == null && !string.IsNullOrWhiteSpace(n))
        {
            var lower = n.ToLowerInvariant();
            user = await _db.Users.FirstOrDefaultAsync(x => x.MinecraftNick != null && x.MinecraftNick.ToLower() == lower, ct);
        }

        if (user == null)
            return Ok(new MinecraftPlayerStatusDto(
                Linked: false,
                Nick: string.IsNullOrWhiteSpace(n) ? null : n,
                Uuid: string.IsNullOrWhiteSpace(u) ? null : u,
                LinkCount: 0,
                Score: 0,
                WeeklyPenaltyCurrent: await GetWeeklyPenaltyAsync(ct),
                PenaltyTotal: 0,
                EffectiveScore: 0,
                ChargedThisWeek: false,
                Debuffed: true
            ));

        var penalty = await GetWeeklyPenaltyAsync(ct);
        var score = await GetUserScoreAsync(user.Id, ct);
        var penaltyTotal = await _db.MinecraftWeeklyJoins
            .AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .SumAsync(x => (int?)x.PenaltyApplied, ct) ?? 0;
        var effectiveScore = score - penaltyTotal;
        var debuffed = effectiveScore < 0;

        // chargedThisWeek тут не считаем
        return Ok(new MinecraftPlayerStatusDto(
            Linked: true,
            Nick: user.MinecraftNick,
            Uuid: user.MinecraftUuid,
            LinkCount: user.MinecraftLinkCount,
            Score: score,
            WeeklyPenaltyCurrent: penalty,
            PenaltyTotal: penaltyTotal,
            EffectiveScore: effectiveScore,
            ChargedThisWeek: false,
            Debuffed: debuffed
        ));
    }
}
