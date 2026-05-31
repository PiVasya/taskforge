using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Services;

public sealed class StudentAccessService
{
    private readonly TelegramQuizDbContext _db;
    private readonly StudentDirectoryService _directory;

    public StudentAccessService(TelegramQuizDbContext db, StudentDirectoryService directory)
    {
        _db = db;
        _directory = directory;
    }

    public async Task<bool> HasAccessAsync(long userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.Whitelist.AnyAsync(x => x.UserId == userId && (x.ExpireTime == null || x.ExpireTime > now), ct);
    }

    public async Task AddAsync(long userId, int? hours, CancellationToken ct)
    {
        await _directory.EnsureStubAsync(userId, ct);

        var entity = await _db.Whitelist.FindAsync([userId], ct);
        if (entity == null)
        {
            entity = new WhitelistEntry { UserId = userId };
            _db.Whitelist.Add(entity);
        }

        entity.ExpireTime = hours is > 0 ? DateTimeOffset.UtcNow.AddHours(hours.Value) : null;
        entity.LastNotification = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveAsync(long userId, CancellationToken ct)
    {
        var entity = await _db.Whitelist.FindAsync([userId], ct);
        if (entity == null) return false;
        _db.Whitelist.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<WhitelistEntry?> GetAsync(long userId, CancellationToken ct)
    {
        return await _db.Whitelist.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }
}
