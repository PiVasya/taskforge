using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;

namespace TelegramQuizBot.Services;

public sealed class StatisticsService
{
    private readonly TelegramQuizDbContext _db;

    public StatisticsService(TelegramQuizDbContext db) => _db = db;

    public async Task<string> BuildClassStatsAsync(CancellationToken ct)
    {
        var rows = await _db.Progress
            .OrderByDescending(x => x.Correct)
            .ThenBy(x => x.Incorrect)
            .Take(50)
            .ToListAsync(ct);

        if (rows.Count == 0) return "Пока нет статистики класса.";

        var lines = new List<string> { "📊 Статистика класса:" };
        foreach (var row in rows)
            lines.Add($"{row.UserId}: ✅ {row.Correct}, ❌ {row.Incorrect}, ⭐ {row.Experience}");

        return string.Join('\n', lines);
    }

    public async Task<string> BuildStartLogAsync(CancellationToken ct)
    {
        var rows = await _db.StartLog
            .OrderByDescending(x => x.Timestamp)
            .Take(50)
            .ToListAsync(ct);

        if (rows.Count == 0) return "Лог запусков пуст.";

        var lines = new List<string> { "🚀 Последние запуски:" };
        foreach (var row in rows)
            lines.Add($"{row.Timestamp:yyyy-MM-dd HH:mm} — {row.UserId} @{row.Username} {row.FullName}".Trim());

        return string.Join('\n', lines);
    }
}
