using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;
using TelegramQuizBot.Services;

namespace TelegramQuizBot.Import;

public sealed class LegacySqliteImportService
{
    private readonly TelegramQuizDbContext _db;
    private readonly StudentDirectoryService _directory;
    private readonly ILogger<LegacySqliteImportService> _logger;

    public LegacySqliteImportService(
        TelegramQuizDbContext db,
        StudentDirectoryService directory,
        ILogger<LegacySqliteImportService> logger)
    {
        _db = db;
        _directory = directory;
        _logger = logger;
    }

    public async Task ImportAsync(string path, CancellationToken ct)
    {
        var files = ResolveFiles(path);
        if (files.Count == 0)
            throw new FileNotFoundException("Legacy SQLite database or directory was not found.", path);

        foreach (var file in files)
        {
            await ImportSingleFileAsync(file, ct);
        }

        var rebuilt = await _directory.RebuildContactsFromExistingDataAsync(ct);
        _logger.LogInformation("Legacy SQLite import completed. Files: {Count}. Student contacts rebuilt/created: {Contacts}", files.Count, rebuilt);
    }

    private async Task ImportSingleFileAsync(string sqlitePath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        _logger.LogInformation("Importing legacy SQLite database {Path}", sqlitePath);

        await ImportCategoriesAsync(connection, ct);
        await ImportTeachersAsync(connection, ct);
        await ImportWhitelistAsync(connection, ct);
        await ImportQuizzesAsync(connection, ct);
        await ImportProgressAsync(connection, ct);
        await ImportUserSettingsAsync(connection, ct);
        await ImportTechnicalBreakAsync(connection, ct);
        await ImportStartLogAsync(connection, ct);
        await ImportCategoryStatsAsync(connection, ct);
        await ImportSubcategoryStatsAsync(connection, ct);
        await ImportSmartProgressAsync(connection, ct);
        await ImportDiagnosticResultsAsync(connection, ct);
        await ImportDiagnosticTestsAsync(connection, ct);
        await ImportTestQuestionsAsync(connection, ct);
        await ImportUserAnswersAsync(connection, ct);
    }

    private static IReadOnlyList<string> ResolveFiles(string path)
    {
        if (File.Exists(path)) return new[] { path };
        if (!Directory.Exists(path)) return Array.Empty<string>();

        return Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
            .Where(x => x.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                     || x.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                     || x.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task ImportCategoriesAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "categories", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, COALESCE(order_index, 0) FROM categories";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (await _db.Categories.AnyAsync(x => x.Name == name, ct)) continue;
            _db.Categories.Add(new Category { Name = name, OrderIndex = reader.GetInt32(1) });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportTeachersAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "authorized_teachers", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM authorized_teachers";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            if (await _db.AuthorizedTeachers.AnyAsync(x => x.UserId == userId, ct)) continue;
            _db.AuthorizedTeachers.Add(new AuthorizedTeacher { UserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportWhitelistAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "whitelist", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, expire_time, last_notification FROM whitelist";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var entity = await _db.Whitelist.FindAsync([userId], ct) ?? new WhitelistEntry { UserId = userId };
            entity.ExpireTime = ParseDateTimeOffset(reader, 1);
            entity.LastNotification = ParseDateTimeOffset(reader, 2);
            if (_db.Entry(entity).State == EntityState.Detached) _db.Whitelist.Add(entity);
            await _directory.EnsureStubAsync(userId, ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportQuizzesAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "quizzes", ct)) return;
        var columns = await GetColumnsAsync(connection, "quizzes", ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM quizzes";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = GetInt64(reader, columns, "id") ?? 0;
            if (id == 0 || await _db.Quizzes.AnyAsync(x => x.Id == id, ct)) continue;

            _db.Quizzes.Add(new QuizQuestion
            {
                Id = id,
                Question = GetString(reader, columns, "question") ?? string.Empty,
                Options = GetString(reader, columns, "options"),
                CorrectOptionId = GetInt32(reader, columns, "correct_option_id"),
                Explanation = GetString(reader, columns, "explanation"),
                Category = GetString(reader, columns, "category") ?? "Остальное",
                Subcategory = GetString(reader, columns, "subcategory") ?? "Без подкатегории",
                Type = GetString(reader, columns, "type") ?? "quiz",
                Answer = GetString(reader, columns, "answer") ?? string.Empty,
                Image = GetString(reader, columns, "image") ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportProgressAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "progress", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, COALESCE(total,0), COALESCE(correct,0), COALESCE(incorrect,0), COALESCE(experience,0) FROM progress";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var entity = await _db.Progress.FindAsync([userId], ct) ?? new ProgressEntry { UserId = userId };
            entity.Total = reader.GetInt32(1);
            entity.Correct = reader.GetInt32(2);
            entity.Incorrect = reader.GetInt32(3);
            entity.Experience = reader.GetInt32(4);
            if (_db.Entry(entity).State == EntityState.Detached) _db.Progress.Add(entity);
            await _directory.EnsureStubAsync(userId, ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportUserSettingsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "user_settings", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, learning_mode, selected_category, diagnostic_completed FROM user_settings";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var entity = await _db.UserSettings.FindAsync([userId], ct) ?? new UserSetting { UserId = userId };
            entity.LearningMode = ReadNullableString(reader, 1) ?? "normal";
            entity.SelectedCategory = ReadNullableString(reader, 2) ?? "all";
            entity.DiagnosticCompleted = !reader.IsDBNull(3) && reader.GetBoolean(3);
            if (_db.Entry(entity).State == EntityState.Detached) _db.UserSettings.Add(entity);
            await _directory.EnsureStubAsync(userId, ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportTechnicalBreakAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "technical_break", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_active, end_time FROM technical_break WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return;
        var entity = await _db.TechnicalBreak.FindAsync([1], ct) ?? new TechnicalBreak { Id = 1 };
        entity.IsActive = !reader.IsDBNull(0) && reader.GetBoolean(0);
        entity.EndTime = ParseDateTimeOffset(reader, 1);
        if (_db.Entry(entity).State == EntityState.Detached) _db.TechnicalBreak.Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportStartLogAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "start_log", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, username, full_name, timestamp FROM start_log";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var timestamp = ParseDateTimeOffset(reader, 3) ?? DateTimeOffset.UtcNow;
            _db.StartLog.Add(new StartLogEntry
            {
                UserId = userId,
                Username = ReadNullableString(reader, 1),
                FullName = ReadNullableString(reader, 2),
                Timestamp = timestamp
            });

            var contact = await _db.StudentContacts.FindAsync([userId], ct) ?? new StudentContact
            {
                UserId = userId,
                ChatId = userId,
                FirstSeenAt = timestamp,
                LastMessageType = "legacy_start_log"
            };

            contact.Username = ReadNullableString(reader, 1);
            contact.FullName = ReadNullableString(reader, 2);
            contact.LastSeenAt = timestamp > contact.LastSeenAt ? timestamp : contact.LastSeenAt;
            contact.LastMessageAt = contact.LastSeenAt;
            contact.MessageCount = Math.Max(1, contact.MessageCount + 1);
            contact.SearchText = StudentDirectoryService.BuildSearchText(contact);

            if (_db.Entry(contact).State == EntityState.Detached) _db.StudentContacts.Add(contact);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportCategoryStatsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "category_stats", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, category, subcategory, correct, incorrect FROM category_stats";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var category = ReadNullableString(reader, 1) ?? "Остальное";
            var subcategory = ReadNullableString(reader, 2) ?? "Без подкатегории";
            var entity = await _db.CategoryStats.FindAsync([userId, category, subcategory], ct)
                ?? new CategoryStat { UserId = userId, Category = category, Subcategory = subcategory };
            entity.Correct = reader.GetInt32(3);
            entity.Incorrect = reader.GetInt32(4);
            if (_db.Entry(entity).State == EntityState.Detached) _db.CategoryStats.Add(entity);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportSubcategoryStatsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "subcategory_stats", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, category, subcategory, correct, incorrect FROM subcategory_stats";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var category = ReadNullableString(reader, 1) ?? "Остальное";
            var subcategory = ReadNullableString(reader, 2) ?? "Без подкатегории";
            var entity = await _db.SubcategoryStats.FindAsync([userId, category, subcategory], ct)
                ?? new SubcategoryStat { UserId = userId, Category = category, Subcategory = subcategory };
            entity.Correct = reader.GetInt32(3);
            entity.Incorrect = reader.GetInt32(4);
            if (_db.Entry(entity).State == EntityState.Detached) _db.SubcategoryStats.Add(entity);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportSmartProgressAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "smart_progress", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, subcategory, correct, incorrect FROM smart_progress";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var subcategory = ReadNullableString(reader, 1) ?? "Без подкатегории";
            var entity = await _db.SmartProgress.FindAsync([userId, subcategory], ct)
                ?? new SmartProgressEntry { UserId = userId, Subcategory = subcategory };
            entity.Correct = reader.GetInt32(2);
            entity.Incorrect = reader.GetInt32(3);
            if (_db.Entry(entity).State == EntityState.Detached) _db.SmartProgress.Add(entity);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportDiagnosticResultsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "diagnostic_results", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, subcategory, correct FROM diagnostic_results";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var userId = reader.GetInt64(0);
            var subcategory = ReadNullableString(reader, 1) ?? "Без подкатегории";
            var entity = await _db.DiagnosticResults.FindAsync([userId, subcategory], ct)
                ?? new DiagnosticResult { UserId = userId, Subcategory = subcategory };
            entity.Correct = !reader.IsDBNull(2) && reader.GetBoolean(2);
            if (_db.Entry(entity).State == EntityState.Detached) _db.DiagnosticResults.Add(entity);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportDiagnosticTestsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "diagnostic_tests", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, description, question_ids FROM diagnostic_tests";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (await _db.DiagnosticTests.AnyAsync(x => x.Id == id, ct)) continue;
            _db.DiagnosticTests.Add(new DiagnosticTest
            {
                Id = id,
                Title = reader.GetString(1),
                Description = ReadNullableString(reader, 2),
                QuestionIds = reader.GetString(3)
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportTestQuestionsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "test_questions", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT test_id, quiz_id FROM test_questions";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var testId = reader.GetInt64(0);
            var quizId = reader.GetInt64(1);
            if (await _db.TestQuestions.AnyAsync(x => x.TestId == testId && x.QuizId == quizId, ct)) continue;
            _db.TestQuestions.Add(new TestQuestion { TestId = testId, QuizId = quizId });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ImportUserAnswersAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "user_answers", ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, username, quiz_id, correct, timestamp FROM user_answers";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            _db.UserAnswers.Add(new UserAnswer
            {
                UserId = reader.GetInt64(0),
                Username = ReadNullableString(reader, 1),
                QuizId = reader.GetInt64(2),
                Correct = !reader.IsDBNull(3) && reader.GetBoolean(3),
                Timestamp = ParseDateTimeOffset(reader, 4) ?? DateTimeOffset.UtcNow
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1";
        command.Parameters.AddWithValue("$table", tableName);
        var result = await command.ExecuteScalarAsync(ct);
        return result != null;
    }

    private static async Task<Dictionary<string, int>> GetColumnsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ordinal = 0;
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(1)] = ordinal++;
        }
        return result;
    }

    private static string? GetString(SqliteDataReader reader, Dictionary<string, int> columns, string column)
    {
        return columns.TryGetValue(column, out var index) && !reader.IsDBNull(index) ? reader.GetString(index) : null;
    }

    private static long? GetInt64(SqliteDataReader reader, Dictionary<string, int> columns, string column)
    {
        return columns.TryGetValue(column, out var index) && !reader.IsDBNull(index) ? reader.GetInt64(index) : null;
    }

    private static int? GetInt32(SqliteDataReader reader, Dictionary<string, int> columns, string column)
    {
        return columns.TryGetValue(column, out var index) && !reader.IsDBNull(index) ? reader.GetInt32(index) : null;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateTimeOffset? ParseDateTimeOffset(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        var value = reader.GetString(index);
        if (DateTimeOffset.TryParse(value, out var dto)) return dto.ToUniversalTime();
        if (DateTime.TryParse(value, out var dt)) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUniversalTime();
        return null;
    }
}
