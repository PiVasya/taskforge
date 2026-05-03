using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Services;

public sealed class TeacherAccessService
{
    private readonly TelegramQuizDbContext _db;

    public TeacherAccessService(TelegramQuizDbContext db) => _db = db;

    public Task<bool> IsTeacherAsync(long userId, CancellationToken ct) =>
        _db.AuthorizedTeachers.AnyAsync(x => x.UserId == userId, ct);

    public async Task AuthorizeAsync(long userId, CancellationToken ct)
    {
        if (await IsTeacherAsync(userId, ct)) return;
        _db.AuthorizedTeachers.Add(new AuthorizedTeacher { UserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync(ct);
    }

    public async Task LogoutAsync(long userId, CancellationToken ct)
    {
        var teacher = await _db.AuthorizedTeachers.FindAsync([userId], ct);
        if (teacher == null) return;
        _db.AuthorizedTeachers.Remove(teacher);
        await _db.SaveChangesAsync(ct);
    }
}
