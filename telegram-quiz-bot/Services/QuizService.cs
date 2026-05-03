using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Services;

public sealed class QuizService
{
    private readonly TelegramQuizDbContext _db;

    public QuizService(TelegramQuizDbContext db) => _db = db;

    public async Task<QuizQuestion> AddPollQuizAsync(
        string question,
        IReadOnlyList<string> options,
        int correctOptionId,
        string? explanation,
        string? category,
        string? subcategory,
        string? imageKey,
        CancellationToken ct)
    {
        var quiz = new QuizQuestion
        {
            Question = question.Trim(),
            Options = string.Join('|', options.Select(x => x.Trim())),
            CorrectOptionId = correctOptionId,
            Explanation = explanation ?? string.Empty,
            Category = CategoryService.Normalize(category, CategoryService.DefaultCategory),
            Subcategory = CategoryService.Normalize(subcategory, CategoryService.DefaultSubcategory),
            Type = "quiz",
            Answer = string.Empty,
            Image = imageKey ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync(ct);
        return quiz;
    }

    public async Task<QuizQuestion> AddTextQuestionAsync(
        string question,
        string answer,
        string? explanation,
        string? category,
        string? subcategory,
        string? imageKey,
        CancellationToken ct)
    {
        var quiz = new QuizQuestion
        {
            Question = question.Trim(),
            Options = null,
            CorrectOptionId = null,
            Explanation = explanation ?? string.Empty,
            Category = CategoryService.Normalize(category, CategoryService.DefaultCategory),
            Subcategory = CategoryService.Normalize(subcategory, CategoryService.DefaultSubcategory),
            Type = "text",
            Answer = answer.Trim(),
            Image = imageKey ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync(ct);
        return quiz;
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken ct)
    {
        var quiz = await _db.Quizzes.FindAsync([id], ct);
        if (quiz == null) return false;
        _db.Quizzes.Remove(quiz);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<List<QuizQuestion>> ListAsync(string? category, int skip, int take, CancellationToken ct)
    {
        IQueryable<QuizQuestion> query = _db.Quizzes.OrderBy(x => x.Id);
        if (!string.IsNullOrWhiteSpace(category) && category != "all")
            query = query.Where(x => x.Category == category);
        return query.Skip(skip).Take(take).ToListAsync(ct);
    }

    public async Task<QuizQuestion?> GetRandomForUserAsync(long userId, string? category, string? preferredSubcategory, CancellationToken ct)
    {
        IQueryable<QuizQuestion> query = _db.Quizzes;
        if (!string.IsNullOrWhiteSpace(category) && category != "all")
            query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(preferredSubcategory))
            query = query.Where(x => x.Subcategory == preferredSubcategory);

        var count = await query.CountAsync(ct);
        if (count == 0) return null;

        var skip = Random.Shared.Next(count);
        return await query.OrderBy(x => x.Id).Skip(skip).FirstAsync(ct);
    }

    public async Task<List<string>> GetWeakSubcategoriesAsync(long userId, CancellationToken ct)
    {
        return await _db.SubcategoryStats
            .Where(x => x.UserId == userId && x.Incorrect > x.Correct)
            .OrderByDescending(x => x.Incorrect - x.Correct)
            .Select(x => x.Subcategory)
            .Distinct()
            .Take(10)
            .ToListAsync(ct);
    }
}
