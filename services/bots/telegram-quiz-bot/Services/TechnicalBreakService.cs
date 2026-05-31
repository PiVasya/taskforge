using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Services;

public sealed class TechnicalBreakService
{
    private readonly TelegramQuizDbContext _db;

    public TechnicalBreakService(TelegramQuizDbContext db) => _db = db;

    public async Task<TechnicalBreak> GetAsync(CancellationToken ct)
    {
        var entity = await _db.TechnicalBreak.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (entity != null) return entity;

        entity = new TechnicalBreak { Id = 1, IsActive = false };
        _db.TechnicalBreak.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct)
    {
        var entity = await GetAsync(ct);
        if (!entity.IsActive) return false;
        if (entity.EndTime is null) return true;
        if (entity.EndTime > DateTimeOffset.UtcNow) return true;

        entity.IsActive = false;
        entity.EndTime = null;
        await _db.SaveChangesAsync(ct);
        return false;
    }

    public async Task SetAsync(DateTimeOffset endTime, CancellationToken ct)
    {
        var entity = await GetAsync(ct);
        entity.IsActive = true;
        entity.EndTime = endTime;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DisableAsync(CancellationToken ct)
    {
        var entity = await GetAsync(ct);
        entity.IsActive = false;
        entity.EndTime = null;
        await _db.SaveChangesAsync(ct);
    }
}
