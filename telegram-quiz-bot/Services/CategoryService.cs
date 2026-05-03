using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Services;

public sealed class CategoryService
{
    private readonly TelegramQuizDbContext _db;
    public const string DefaultCategory = "Остальное";
    public const string DefaultSubcategory = "Без подкатегории";

    public CategoryService(TelegramQuizDbContext db) => _db = db;

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct)
    {
        var fromTable = await _db.Categories.OrderBy(x => x.OrderIndex).Select(x => x.Name).ToListAsync(ct);
        if (fromTable.Count > 0) return fromTable;

        var fromQuizzes = await _db.Quizzes.Select(x => x.Category).Distinct().OrderBy(x => x).ToListAsync(ct);
        return fromQuizzes.Count > 0 ? fromQuizzes : [DefaultCategory];
    }

    public async Task AddCategoryAsync(string name, CancellationToken ct)
    {
        name = Normalize(name, DefaultCategory);
        if (await _db.Categories.AnyAsync(x => x.Name == name, ct)) return;
        var maxOrder = await _db.Categories.Select(x => (int?)x.OrderIndex).MaxAsync(ct) ?? 0;
        _db.Categories.Add(new Category { Name = name, OrderIndex = maxOrder + 1 });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveCategoryAsync(string name, CancellationToken ct)
    {
        var category = await _db.Categories.FindAsync([name], ct);
        if (category == null) return false;
        _db.Categories.Remove(category);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
