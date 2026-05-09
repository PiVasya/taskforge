using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Services;

public sealed record QuizCategoryCount(string Name, int Count);
public sealed record QuizSubcategoryCount(string Name, int Count);
public sealed record NextQuizResult(QuizQuestion? Quiz, int TotalAvailable, int UnseenAvailable, bool IsRepeatCycle, bool FilterWasRelaxed);

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

    public Task<QuizQuestion?> GetByIdAsync(long id, CancellationToken ct)
    {
        return _db.Quizzes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public Task<List<QuizQuestion>> ListAsync(string? category, int skip, int take, CancellationToken ct)
    {
        return ListAsync(category, null, skip, take, ct);
    }

    public Task<List<QuizQuestion>> ListAsync(string? category, string? subcategory, int skip, int take, CancellationToken ct)
    {
        return BuildFilteredQuery(category, subcategory)
            .OrderBy(x => x.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<int> CountAsync(string? category, string? subcategory, CancellationToken ct)
    {
        return BuildFilteredQuery(category, subcategory).CountAsync(ct);
    }

    public async Task<List<QuizCategoryCount>> GetCategoryStatsAsync(CancellationToken ct)
    {
        return await _db.Quizzes
            .AsNoTracking()
            .GroupBy(x => x.Category)
            .Select(g => new QuizCategoryCount(g.Key, g.Count()))
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
    }

    public async Task<List<QuizSubcategoryCount>> GetSubcategoryStatsAsync(string? category, CancellationToken ct)
    {
        IQueryable<QuizQuestion> query = _db.Quizzes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category) && category != "all")
            query = query.Where(x => x.Category == category);

        return await query
            .GroupBy(x => x.Subcategory)
            .Select(g => new QuizSubcategoryCount(g.Key, g.Count()))
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
    }

    public async Task<int> CountWithImagesAsync(CancellationToken ct)
    {
        return await _db.Quizzes.AsNoTracking().CountAsync(x => x.Image != string.Empty, ct);
    }

    public async Task<int> CountMissingAnswerAsync(CancellationToken ct)
    {
        return await _db.Quizzes.AsNoTracking().CountAsync(x => x.Answer == string.Empty && x.CorrectOptionId == null, ct);
    }

    public async Task<NextQuizResult> GetNextForUserAsync(long userId, string? category, string? preferredSubcategory, CancellationToken ct)
    {
        var requestedQuery = BuildFilteredQuery(category, preferredSubcategory);
        var requestedTotal = await requestedQuery.CountAsync(ct);

        if (requestedTotal > 0)
        {
            var requestedUnseen = ExcludeAnswered(requestedQuery, userId);
            var requestedUnseenCount = await requestedUnseen.CountAsync(ct);
            if (requestedUnseenCount > 0)
            {
                return new NextQuizResult(
                    await PickRandomAsync(requestedUnseen, requestedUnseenCount, ct),
                    requestedTotal,
                    requestedUnseenCount,
                    IsRepeatCycle: false,
                    FilterWasRelaxed: false);
            }
        }

        var categoryQuery = BuildFilteredQuery(category, null);
        var categoryTotal = await categoryQuery.CountAsync(ct);
        if (categoryTotal > 0)
        {
            var categoryUnseen = ExcludeAnswered(categoryQuery, userId);
            var categoryUnseenCount = await categoryUnseen.CountAsync(ct);
            if (categoryUnseenCount > 0)
            {
                return new NextQuizResult(
                    await PickRandomAsync(categoryUnseen, categoryUnseenCount, ct),
                    categoryTotal,
                    categoryUnseenCount,
                    IsRepeatCycle: false,
                    FilterWasRelaxed: requestedTotal == 0 || !string.IsNullOrWhiteSpace(preferredSubcategory));
            }

            return new NextQuizResult(
                await PickRandomAsync(categoryQuery, categoryTotal, ct),
                categoryTotal,
                0,
                IsRepeatCycle: true,
                FilterWasRelaxed: requestedTotal == 0 || !string.IsNullOrWhiteSpace(preferredSubcategory));
        }

        var allQuery = BuildFilteredQuery(null, null);
        var allTotal = await allQuery.CountAsync(ct);
        if (allTotal == 0) return new NextQuizResult(null, 0, 0, false, false);

        var allUnseen = ExcludeAnswered(allQuery, userId);
        var allUnseenCount = await allUnseen.CountAsync(ct);
        if (allUnseenCount > 0)
        {
            return new NextQuizResult(
                await PickRandomAsync(allUnseen, allUnseenCount, ct),
                allTotal,
                allUnseenCount,
                IsRepeatCycle: false,
                FilterWasRelaxed: true);
        }

        return new NextQuizResult(
            await PickRandomAsync(allQuery, allTotal, ct),
            allTotal,
            0,
            IsRepeatCycle: true,
            FilterWasRelaxed: true);
    }

    public async Task<QuizQuestion?> GetRandomForUserAsync(long userId, string? category, string? preferredSubcategory, CancellationToken ct)
    {
        return (await GetNextForUserAsync(userId, category, preferredSubcategory, ct)).Quiz;
    }

    public async Task<List<string>> GetWeakSubcategoriesAsync(long userId, CancellationToken ct)
    {
        return await _db.SubcategoryStats
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.Incorrect > x.Correct)
            .Select(x => new { x.Subcategory, Score = x.Incorrect - x.Correct })
            .GroupBy(x => x.Subcategory)
            .Select(g => new { Subcategory = g.Key, Score = g.Max(x => x.Score) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Subcategory)
            .Take(10)
            .Select(x => x.Subcategory)
            .ToListAsync(ct);
    }

    private IQueryable<QuizQuestion> BuildFilteredQuery(string? category, string? subcategory)
    {
        IQueryable<QuizQuestion> query = _db.Quizzes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category) && category != "all")
            query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(subcategory) && subcategory != "all")
            query = query.Where(x => x.Subcategory == subcategory);
        return query;
    }

    private IQueryable<QuizQuestion> ExcludeAnswered(IQueryable<QuizQuestion> query, long userId)
    {
        return query.Where(q => !_db.UserAnswers.Any(a => a.UserId == userId && a.QuizId == q.Id));
    }

    private static Task<QuizQuestion> PickRandomAsync(IQueryable<QuizQuestion> query, int count, CancellationToken ct)
    {
        var skip = Random.Shared.Next(count);
        return query.OrderBy(x => x.Id).Skip(skip).FirstAsync(ct);
    }
}
