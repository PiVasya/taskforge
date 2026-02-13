using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Me;

[ApiController]
[Route("api/me/ui-settings")]
[Authorize]
public class UiSettingsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _cur;

    public UiSettingsController(ApplicationDbContext db, ICurrentUserService cur)
    {
        _db = db;
        _cur = cur;
    }

    public record UiSettingsDto(
        string? ColorTheme,
        string? Mode,
        bool? BgFx,
        string? FxMode,
        int? FxVariant
    );

    [HttpGet]
    public async Task<ActionResult<UiSettingsDto>> Get()
    {
        var uid = _cur.GetUserId();
        var ent = await _db.UserUiSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid);
        if (ent is null)
        {
            // дефолты
            return Ok(new UiSettingsDto("blue", "light", false, "random", 3));
        }

        return Ok(new UiSettingsDto(ent.ColorTheme, ent.Mode, ent.BgFx, ent.FxMode, ent.FxVariant));
    }

    [HttpPut]
    public async Task<ActionResult<UiSettingsDto>> Put([FromBody] UiSettingsDto dto)
    {
        var uid = _cur.GetUserId();
        var ent = await _db.UserUiSettings.FirstOrDefaultAsync(x => x.UserId == uid);
        if (ent is null)
        {
            ent = new UserUiSettings { UserId = uid };
            _db.UserUiSettings.Add(ent);
        }

        // мягкая валидация, чтобы не ломать
        if (!string.IsNullOrWhiteSpace(dto.ColorTheme)) ent.ColorTheme = dto.ColorTheme;
        if (!string.IsNullOrWhiteSpace(dto.Mode)) ent.Mode = dto.Mode;
        if (dto.BgFx.HasValue) ent.BgFx = dto.BgFx.Value;
        if (!string.IsNullOrWhiteSpace(dto.FxMode)) ent.FxMode = dto.FxMode;
        if (dto.FxVariant.HasValue) ent.FxVariant = dto.FxVariant.Value;

        ent.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new UiSettingsDto(ent.ColorTheme, ent.Mode, ent.BgFx, ent.FxMode, ent.FxVariant));
    }
}
