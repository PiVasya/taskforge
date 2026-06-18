using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;
using TelegramQuizBot.Bot;

namespace TelegramQuizBot.Services;

public sealed record QuizCategoryCount(string Name, int Count);
public sealed record QuizSubcategoryCount(string Name, int Count);
public sealed record SmartRecommendation(string Category, string Subcategory, int Correct, int Incorrect, int Total, double Accuracy, int QuestionCount);
public sealed record TopicProgressItem(string Category, string Subcategory, int Correct, int Incorrect, int Total, double Accuracy, int QuestionCount);
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
        TelegramDebugTrace.Write("quiz.service", "add-poll", ("quizId", quiz.Id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("question", quiz.Question), ("options", quiz.Options), ("correctOptionId", quiz.CorrectOptionId));
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
        TelegramDebugTrace.Write("quiz.service", "add-text", ("quizId", quiz.Id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("question", quiz.Question), ("answer", quiz.Answer));
        return quiz;
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken ct)
    {
        var quiz = await _db.Quizzes.FindAsync([id], ct);
        if (quiz == null)
        {
            TelegramDebugTrace.Write("quiz.service", "remove:not-found", ("quizId", id));
            return false;
        }
        TelegramDebugTrace.Write("quiz.service", "remove", ("quizId", id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("question", quiz.Question));
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
        TelegramDebugTrace.Write("quiz.service", "list", ("category", category), ("subcategory", subcategory), ("skip", skip), ("take", take));
        return BuildFilteredQuery(category, subcategory)
            .OrderBy(x => x.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<int> CountAsync(string? category, string? subcategory, CancellationToken ct)
    {
        var count = await BuildFilteredQuery(category, subcategory).CountAsync(ct);
        TelegramDebugTrace.Write("quiz.service", "count", ("category", category), ("subcategory", subcategory), ("count", count));
        return count;
    }

    public async Task<List<QuizCategoryCount>> GetCategoryStatsAsync(CancellationToken ct)
    {
        // EF Core/Npgsql can translate grouping into anonymous DTOs, but not always
        // directly into positional record constructors. Keep SQL translation simple,
        // then map to the public record on the client side.
        var rows = await _db.Quizzes
            .AsNoTracking()
            .GroupBy(x => x.Category)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count()
            })
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var result = rows
            .Select(x => new QuizCategoryCount(x.Name, x.Count))
            .ToList();
        TelegramDebugTrace.Write("quiz.service", "category-stats", ("count", result.Count), ("items", string.Join(",", result.Select(x => $"{x.Name}:{x.Count}"))));
        return result;
    }

    public async Task<List<QuizSubcategoryCount>> GetSubcategoryStatsAsync(string? category, CancellationToken ct)
    {
        IQueryable<QuizQuestion> query = _db.Quizzes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category) && category != "all")
            query = query.Where(x => x.Category == category);

        // Same as categories: first translate to a simple anonymous shape,
        // then map to the record after data is loaded.
        var rows = await query
            .GroupBy(x => x.Subcategory)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count()
            })
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var result = rows
            .Select(x => new QuizSubcategoryCount(x.Name, x.Count))
            .ToList();
        TelegramDebugTrace.Write("quiz.service", "subcategory-stats", ("category", category), ("count", result.Count), ("items", string.Join(",", result.Select(x => $"{x.Name}:{x.Count}"))));
        return result;
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
        TelegramDebugTrace.Write("quiz.service", "next:start", ("userId", userId), ("category", category), ("preferredSubcategory", preferredSubcategory));
        var requestedQuery = BuildFilteredQuery(category, preferredSubcategory);
        var requestedTotal = await requestedQuery.CountAsync(ct);
        TelegramDebugTrace.Write("quiz.service", "next:requested-total", ("userId", userId), ("category", category), ("preferredSubcategory", preferredSubcategory), ("requestedTotal", requestedTotal));

        if (requestedTotal > 0)
        {
            var requestedUnseen = ExcludeAnswered(requestedQuery, userId);
            var requestedUnseenCount = await requestedUnseen.CountAsync(ct);
            TelegramDebugTrace.Write("quiz.service", "next:requested-unseen", ("userId", userId), ("requestedUnseenCount", requestedUnseenCount));
            if (requestedUnseenCount > 0)
            {
                var picked = await PickRandomAsync(requestedUnseen, requestedUnseenCount, ct);
                TelegramDebugTrace.Write("quiz.service", "next:picked-requested", ("userId", userId), ("quizId", picked.Id), ("category", picked.Category), ("subcategory", picked.Subcategory));
                return new NextQuizResult(
                    picked,
                    requestedTotal,
                    requestedUnseenCount,
                    IsRepeatCycle: false,
                    FilterWasRelaxed: false);
            }
        }

        var categoryQuery = BuildFilteredQuery(category, null);
        var categoryTotal = await categoryQuery.CountAsync(ct);
        TelegramDebugTrace.Write("quiz.service", "next:category-total", ("userId", userId), ("category", category), ("categoryTotal", categoryTotal));
        if (categoryTotal > 0)
        {
            var categoryUnseen = ExcludeAnswered(categoryQuery, userId);
            var categoryUnseenCount = await categoryUnseen.CountAsync(ct);
            TelegramDebugTrace.Write("quiz.service", "next:category-unseen", ("userId", userId), ("categoryUnseenCount", categoryUnseenCount));
            if (categoryUnseenCount > 0)
            {
                var picked = await PickRandomAsync(categoryUnseen, categoryUnseenCount, ct);
                TelegramDebugTrace.Write("quiz.service", "next:picked-category", ("userId", userId), ("quizId", picked.Id), ("category", picked.Category), ("subcategory", picked.Subcategory));
                return new NextQuizResult(
                    picked,
                    categoryTotal,
                    categoryUnseenCount,
                    IsRepeatCycle: false,
                    FilterWasRelaxed: requestedTotal == 0 || !string.IsNullOrWhiteSpace(preferredSubcategory));
            }

            var pickedRepeat = await PickRandomAsync(categoryQuery, categoryTotal, ct);
            TelegramDebugTrace.Write("quiz.service", "next:picked-category-repeat", ("userId", userId), ("quizId", pickedRepeat.Id), ("category", pickedRepeat.Category), ("subcategory", pickedRepeat.Subcategory));
            return new NextQuizResult(
                pickedRepeat,
                categoryTotal,
                0,
                IsRepeatCycle: true,
                FilterWasRelaxed: requestedTotal == 0 || !string.IsNullOrWhiteSpace(preferredSubcategory));
        }

        var allQuery = BuildFilteredQuery(null, null);
        var allTotal = await allQuery.CountAsync(ct);
        TelegramDebugTrace.Write("quiz.service", "next:all-total", ("userId", userId), ("allTotal", allTotal));
        if (allTotal == 0)
        {
            TelegramDebugTrace.Write("quiz.service", "next:empty-database", ("userId", userId));
            return new NextQuizResult(null, 0, 0, false, false);
        }

        var allUnseen = ExcludeAnswered(allQuery, userId);
        var allUnseenCount = await allUnseen.CountAsync(ct);
        TelegramDebugTrace.Write("quiz.service", "next:all-unseen", ("userId", userId), ("allUnseenCount", allUnseenCount));
        if (allUnseenCount > 0)
        {
            var picked = await PickRandomAsync(allUnseen, allUnseenCount, ct);
            TelegramDebugTrace.Write("quiz.service", "next:picked-all", ("userId", userId), ("quizId", picked.Id), ("category", picked.Category), ("subcategory", picked.Subcategory));
            return new NextQuizResult(
                picked,
                allTotal,
                allUnseenCount,
                IsRepeatCycle: false,
                FilterWasRelaxed: true);
        }

        var pickedAllRepeat = await PickRandomAsync(allQuery, allTotal, ct);
        TelegramDebugTrace.Write("quiz.service", "next:picked-all-repeat", ("userId", userId), ("quizId", pickedAllRepeat.Id), ("category", pickedAllRepeat.Category), ("subcategory", pickedAllRepeat.Subcategory));
        return new NextQuizResult(
            pickedAllRepeat,
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
        var recommendations = await GetSmartRecommendationsAsync(userId, 10, ct);
        TelegramDebugTrace.Write("quiz.service", "weak-subcategories", ("userId", userId), ("count", recommendations.Count), ("items", string.Join(",", recommendations.Select(x => x.Subcategory))));
        return recommendations
            .Select(x => x.Subcategory)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<TopicProgressItem>> GetTopicProgressAsync(long userId, bool includeUnanswered, int take, CancellationToken ct)
    {
        take = System.Math.Max(1, take);

        var questionCounts = await GetTopicQuestionCountsAsync(ct);
        var statRows = await _db.SubcategoryStats
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);

        var statMap = statRows.ToDictionary(x => (x.Category, x.Subcategory));
        var result = new List<TopicProgressItem>();

        foreach (var q in questionCounts)
        {
            statMap.TryGetValue((q.Category, q.Subcategory), out var stat);
            var correct = stat?.Correct ?? 0;
            var incorrect = stat?.Incorrect ?? 0;
            var total = correct + incorrect;
            if (!includeUnanswered && total == 0) continue;

            var accuracy = total == 0 ? 0 : correct * 100.0 / total;
            result.Add(new TopicProgressItem(q.Category, q.Subcategory, correct, incorrect, total, accuracy, q.QuestionCount));
        }

        result = includeUnanswered
            ? result
                .OrderByDescending(x => x.Total > 0)
                .ThenBy(x => x.Category)
                .ThenBy(x => x.Subcategory)
                .Take(take)
                .ToList()
            : result
                .OrderBy(x => x.Accuracy)
                .ThenByDescending(x => x.Incorrect)
                .ThenBy(x => x.Total)
                .ThenBy(x => x.Category)
                .ThenBy(x => x.Subcategory)
                .Take(take)
                .ToList();

        TelegramDebugTrace.Write(
            "quiz.service",
            "topic-progress",
            ("userId", userId),
            ("includeUnanswered", includeUnanswered),
            ("take", take),
            ("count", result.Count),
            ("items", string.Join(";", result.Select(x => $"{x.Category}/{x.Subcategory}:{x.Correct}/{x.Total}:{x.QuestionCount}"))));

        return result;
    }

    public async Task<List<SmartRecommendation>> GetSmartRecommendationsAsync(long userId, int take, CancellationToken ct)
    {
        take = System.Math.Max(1, take);

        var questionCounts = await GetTopicQuestionCountsAsync(ct);
        var countMap = questionCounts.ToDictionary(
            x => (x.Category, x.Subcategory),
            x => x.QuestionCount);

        var statRows = await _db.SubcategoryStats
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);

        var attempted = statRows
            .Select(x =>
            {
                var total = x.Correct + x.Incorrect;
                var accuracy = total == 0 ? 0 : x.Correct * 100.0 / total;
                countMap.TryGetValue((x.Category, x.Subcategory), out var questionCount);
                return new SmartRecommendation(x.Category, x.Subcategory, x.Correct, x.Incorrect, total, accuracy, questionCount);
            })
            .Where(x => x.QuestionCount > 0 && x.Total > 0)
            .ToList();

        var result = attempted
            .Where(x => x.Incorrect > 0)
            .OrderBy(x => x.Accuracy)
            .ThenByDescending(x => x.Incorrect)
            .ThenBy(x => x.Total)
            .ThenByDescending(x => x.QuestionCount)
            .ThenBy(x => x.Category)
            .ThenBy(x => x.Subcategory)
            .Take(take)
            .ToList();

        var used = result
            .Select(x => (x.Category, x.Subcategory))
            .ToHashSet();

        foreach (var q in questionCounts
                     .Where(x => !attempted.Any(a => a.Category == x.Category && a.Subcategory == x.Subcategory))
                     .OrderBy(x => x.Category)
                     .ThenBy(x => x.Subcategory))
        {
            if (result.Count >= take) break;
            if (!used.Add((q.Category, q.Subcategory))) continue;
            result.Add(new SmartRecommendation(q.Category, q.Subcategory, 0, 0, 0, 0, q.QuestionCount));
        }

        foreach (var item in attempted
                     .Where(x => !used.Contains((x.Category, x.Subcategory)))
                     .OrderBy(x => x.Total)
                     .ThenBy(x => x.Accuracy)
                     .ThenByDescending(x => x.QuestionCount))
        {
            if (result.Count >= take) break;
            if (!used.Add((item.Category, item.Subcategory))) continue;
            result.Add(item);
        }

        TelegramDebugTrace.Write("quiz.service", "smart-recommendations", ("userId", userId), ("take", take), ("resultCount", result.Count), ("items", string.Join(";", result.Select(x => $"{x.Category}/{x.Subcategory}:{x.Accuracy:0.#}%:{x.Correct}/{x.Total}:{x.QuestionCount}"))));
        return result;
    }

    private async Task<List<TopicQuestionCount>> GetTopicQuestionCountsAsync(CancellationToken ct)
    {
        var rows = await _db.Quizzes
            .AsNoTracking()
            .GroupBy(x => new { x.Category, x.Subcategory })
            .Select(g => new
            {
                g.Key.Category,
                g.Key.Subcategory,
                QuestionCount = g.Count()
            })
            .OrderBy(x => x.Category)
            .ThenBy(x => x.Subcategory)
            .ToListAsync(ct);

        return rows
            .Select(x => new TopicQuestionCount(x.Category, x.Subcategory, x.QuestionCount))
            .ToList();
    }

    private sealed record TopicQuestionCount(string Category, string Subcategory, int QuestionCount);

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
        TelegramDebugTrace.Write("quiz.service", "pick-random", ("count", count), ("skip", skip));
        return query.OrderBy(x => x.Id).Skip(skip).FirstAsync(ct);
    }
}
