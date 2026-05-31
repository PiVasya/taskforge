using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;
using TelegramQuizBot.Bot;

namespace TelegramQuizBot.Services;

public sealed class ProgressService
{
    private readonly TelegramQuizDbContext _db;

    public ProgressService(TelegramQuizDbContext db) => _db = db;

    public async Task SaveAnswerAsync(long userId, string? username, QuizQuestion quiz, bool correct, CancellationToken ct)
    {
        TelegramDebugTrace.Write("progress.service", "save-answer:start", ("userId", userId), ("username", username), ("quizId", quiz.Id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("correct", correct));
        var progress = await _db.Progress.FindAsync([userId], ct);
        if (progress == null)
        {
            progress = new ProgressEntry { UserId = userId };
            _db.Progress.Add(progress);
            TelegramDebugTrace.Write("progress.service", "progress-created", ("userId", userId));
        }

        progress.Total++;
        if (correct)
        {
            progress.Correct++;
            progress.Experience += 10;
        }
        else
        {
            progress.Incorrect++;
        }

        var sub = await _db.SubcategoryStats.FindAsync([userId, quiz.Category, quiz.Subcategory], ct);
        if (sub == null)
        {
            sub = new SubcategoryStat { UserId = userId, Category = quiz.Category, Subcategory = quiz.Subcategory };
            _db.SubcategoryStats.Add(sub);
        }

        if (correct) sub.Correct++; else sub.Incorrect++;

        _db.UserAnswers.Add(new UserAnswer
        {
            UserId = userId,
            Username = username,
            QuizId = quiz.Id,
            Correct = correct,
            Timestamp = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        TelegramDebugTrace.Write("progress.service", "save-answer:done", ("userId", userId), ("quizId", quiz.Id), ("total", progress.Total), ("correct", progress.Correct), ("incorrect", progress.Incorrect), ("experience", progress.Experience));
    }

    public async Task<ProgressEntry> GetProgressAsync(long userId, CancellationToken ct)
    {
        var progress = await _db.Progress.FirstOrDefaultAsync(x => x.UserId == userId, ct)
            ?? new ProgressEntry { UserId = userId };
        TelegramDebugTrace.Write("progress.service", "get-progress", ("userId", userId), ("total", progress.Total), ("correct", progress.Correct), ("incorrect", progress.Incorrect), ("experience", progress.Experience));
        return progress;
    }
}
