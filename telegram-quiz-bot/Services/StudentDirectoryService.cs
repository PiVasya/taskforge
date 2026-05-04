using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Services;

public sealed class StudentDirectoryService
{
    private const int MaxSearchCandidates = 5000;
    private readonly TelegramQuizDbContext _db;

    public StudentDirectoryService(TelegramQuizDbContext db)
    {
        _db = db;
    }

    public async Task<StudentContact> RememberMessageAsync(Message message, CancellationToken ct)
    {
        var user = message.From ?? throw new InvalidOperationException("Telegram message has no sender.");
        var now = DateTimeOffset.UtcNow;
        var contact = await _db.StudentContacts.FindAsync([user.Id], ct);
        var isNew = contact == null;

        if (contact == null)
        {
            contact = new StudentContact
            {
                UserId = user.Id,
                FirstSeenAt = now
            };
            _db.StudentContacts.Add(contact);
        }

        contact.ChatId = message.Chat.Id;
        contact.Username = NullIfBlank(user.Username);
        contact.FirstName = NullIfBlank(user.FirstName);
        contact.LastName = NullIfBlank(user.LastName);
        contact.FullName = BuildFullName(user.FirstName, user.LastName);
        contact.LanguageCode = NullIfBlank(user.LanguageCode);
        contact.LastSeenAt = now;
        contact.LastMessageAt = now;
        contact.LastMessageType = message.Type.ToString();
        contact.LastMessageText = TrimForStorage(message.Text ?? message.Caption, 2048);
        contact.MessageCount = isNew ? 1 : contact.MessageCount + 1;
        contact.SearchText = BuildSearchText(contact);

        await _db.SaveChangesAsync(ct);
        return contact;
    }

    public async Task<StudentContact> EnsureStubAsync(long userId, CancellationToken ct)
    {
        var existing = await _db.StudentContacts.FindAsync([userId], ct);
        if (existing != null) return existing;

        var now = DateTimeOffset.UtcNow;
        var contact = new StudentContact
        {
            UserId = userId,
            ChatId = userId,
            FullName = $"Telegram ID {userId}",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastMessageAt = now,
            LastMessageType = "manual",
            MessageCount = 0,
            SearchText = NormalizeForSearch($"{userId} Telegram ID {userId}")
        };

        _db.StudentContacts.Add(contact);
        await _db.SaveChangesAsync(ct);
        return contact;
    }

    public async Task<StudentContact?> GetAsync(long userId, CancellationToken ct)
    {
        return await _db.StudentContacts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public async Task<StudentDirectoryPage> SearchAsync(string? query, int limit, bool includeHidden, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 30);
        var normalizedQuery = NormalizeForSearch(query ?? string.Empty);

        var baseQuery = _db.StudentContacts.AsNoTracking();
        if (!includeHidden) baseQuery = baseQuery.Where(x => !x.IsHidden);

        List<StudentContact> contacts;
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            contacts = await baseQuery
                .OrderByDescending(x => x.LastSeenAt)
                .Take(limit)
                .ToListAsync(ct);
        }
        else
        {
            contacts = await baseQuery
                .OrderByDescending(x => x.LastSeenAt)
                .Take(MaxSearchCandidates)
                .ToListAsync(ct);
        }

        var now = DateTimeOffset.UtcNow;
        var userIds = contacts.Select(x => x.UserId).ToArray();
        var access = await _db.Whitelist.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, x => x, ct);

        var items = contacts
            .Select(contact => ToSearchItem(contact, access.GetValueOrDefault(contact.UserId), normalizedQuery, now))
            .Where(item => string.IsNullOrWhiteSpace(normalizedQuery) || item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Contact.LastSeenAt)
            .Take(limit)
            .ToList();

        return new StudentDirectoryPage(query, items, normalizedQuery.Length > 0);
    }

    public async Task<StudentContactCard?> GetCardAsync(long userId, CancellationToken ct)
    {
        var contact = await _db.StudentContacts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (contact == null) return null;

        var whitelist = await _db.Whitelist.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var progress = await _db.Progress.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return new StudentContactCard(contact, BuildAccessState(whitelist, DateTimeOffset.UtcNow), progress);
    }

    public async Task<int> HideContactAsync(long userId, long teacherId, CancellationToken ct)
    {
        var contact = await _db.StudentContacts.FindAsync([userId], ct);
        if (contact == null) return 0;
        contact.IsHidden = true;
        contact.HiddenAt = DateTimeOffset.UtcNow;
        contact.HiddenByTeacherId = teacherId;
        return await _db.SaveChangesAsync(ct);
    }

    public async Task<int> RestoreContactAsync(long userId, CancellationToken ct)
    {
        var contact = await _db.StudentContacts.FindAsync([userId], ct);
        if (contact == null) return 0;
        contact.IsHidden = false;
        contact.HiddenAt = null;
        contact.HiddenByTeacherId = null;
        return await _db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteContactAsync(long userId, bool deleteAccess, bool deleteProgress, bool deleteLogs, CancellationToken ct)
    {
        var deleted = 0;

        var contact = await _db.StudentContacts.FindAsync([userId], ct);
        if (contact != null)
        {
            _db.StudentContacts.Remove(contact);
            deleted++;
        }

        if (deleteAccess)
        {
            deleted += await _db.Whitelist.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        }

        if (deleteProgress)
        {
            deleted += await _db.Progress.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            deleted += await _db.UserSettings.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            deleted += await _db.CategoryStats.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            deleted += await _db.SubcategoryStats.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            deleted += await _db.SmartProgress.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            deleted += await _db.DiagnosticResults.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            deleted += await _db.UserAnswers.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        }

        if (deleteLogs)
        {
            deleted += await _db.StartLog.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        }

        await _db.SaveChangesAsync(ct);
        return deleted;
    }

    public async Task<int> DeleteContactsWithoutActiveAccessAsync(int olderThanDays, bool includeHidden, CancellationToken ct)
    {
        olderThanDays = Math.Clamp(olderThanDays, 0, 3650);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var now = DateTimeOffset.UtcNow;

        var activeIds = await _db.Whitelist.AsNoTracking()
            .Where(x => x.ExpireTime == null || x.ExpireTime > now)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        var query = _db.StudentContacts.Where(x => x.LastSeenAt < cutoff && !activeIds.Contains(x.UserId));
        if (!includeHidden) query = query.Where(x => !x.IsHidden);

        return await query.ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteHiddenContactsAsync(int olderThanDays, CancellationToken ct)
    {
        olderThanDays = Math.Clamp(olderThanDays, 0, 3650);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        return await _db.StudentContacts
            .Where(x => x.IsHidden && (x.HiddenAt == null || x.HiddenAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteStartLogsOlderThanAsync(int olderThanDays, CancellationToken ct)
    {
        olderThanDays = Math.Clamp(olderThanDays, 0, 3650);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        return await _db.StartLog.Where(x => x.Timestamp < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task<int> RebuildContactsFromExistingDataAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = 0;

        var startRows = await _db.StartLog.AsNoTracking().ToListAsync(ct);
        var starts = startRows
            .GroupBy(x => x.UserId)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(x => x.Timestamp).ToList();
                return new
                {
                    UserId = g.Key,
                    Last = ordered[0],
                    FirstSeenAt = g.Min(x => x.Timestamp),
                    LastSeenAt = g.Max(x => x.Timestamp),
                    Count = g.Count()
                };
            })
            .ToList();

        foreach (var row in starts)
        {
            var contact = await _db.StudentContacts.FindAsync([row.UserId], ct);
            if (contact == null)
            {
                contact = new StudentContact
                {
                    UserId = row.UserId,
                    ChatId = row.UserId,
                    FirstSeenAt = row.FirstSeenAt,
                    MessageCount = row.Count
                };
                _db.StudentContacts.Add(contact);
            }

            contact.Username = row.Last.Username;
            contact.FullName = row.Last.FullName;
            contact.LastSeenAt = row.LastSeenAt;
            contact.LastMessageAt = row.LastSeenAt;
            contact.LastMessageType = "legacy_start_log";
            contact.SearchText = BuildSearchText(contact);
            affected++;
        }

        var knownIds = await _db.StudentContacts.Select(x => x.UserId).ToListAsync(ct);
        var idsFromWhitelist = await _db.Whitelist.AsNoTracking().Select(x => x.UserId).ToListAsync(ct);
        var idsFromProgress = await _db.Progress.AsNoTracking().Select(x => x.UserId).ToListAsync(ct);
        var missingIds = idsFromWhitelist.Concat(idsFromProgress).Distinct().Where(x => !knownIds.Contains(x)).ToList();

        foreach (var userId in missingIds)
        {
            _db.StudentContacts.Add(new StudentContact
            {
                UserId = userId,
                ChatId = userId,
                FullName = $"Telegram ID {userId}",
                FirstSeenAt = now,
                LastSeenAt = now,
                LastMessageAt = now,
                LastMessageType = "legacy_stub",
                SearchText = NormalizeForSearch($"{userId} Telegram ID {userId}")
            });
            affected++;
        }

        await _db.SaveChangesAsync(ct);
        return affected;
    }

    private static StudentContactSearchItem ToSearchItem(StudentContact contact, WhitelistEntry? whitelist, string normalizedQuery, DateTimeOffset now)
    {
        var state = BuildAccessState(whitelist, now);
        var score = string.IsNullOrWhiteSpace(normalizedQuery) ? 1 : Score(contact, normalizedQuery);
        return new StudentContactSearchItem(contact, state, score);
    }

    private static StudentAccessState BuildAccessState(WhitelistEntry? whitelist, DateTimeOffset now)
    {
        if (whitelist == null) return StudentAccessState.NoAccess;
        if (whitelist.ExpireTime == null) return StudentAccessState.Permanent;
        return whitelist.ExpireTime > now
            ? StudentAccessState.Temporary
            : StudentAccessState.Expired;
    }

    private static int Score(StudentContact contact, string normalizedQuery)
    {
        var query = normalizedQuery.Trim();
        if (query.Length == 0) return 1;

        var userId = contact.UserId.ToString(CultureInfo.InvariantCulture);
        if (userId == query) return 1000;
        if (userId.Contains(query, StringComparison.Ordinal)) return 900;

        var haystack = NormalizeForSearch(contact.SearchText);
        if (haystack.Length == 0) haystack = BuildSearchText(contact);

        if (haystack == query) return 850;
        if (haystack.Contains(query, StringComparison.Ordinal)) return 760;

        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hayTokens = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bestTokenScore = 0;

        foreach (var q in queryTokens)
        {
            foreach (var h in hayTokens)
            {
                if (h == q) bestTokenScore = Math.Max(bestTokenScore, 730);
                else if (h.StartsWith(q, StringComparison.Ordinal)) bestTokenScore = Math.Max(bestTokenScore, 690);
                else bestTokenScore = Math.Max(bestTokenScore, Similarity(q, h));
            }
        }

        var wholeScore = Similarity(query, haystack);
        return Math.Max(bestTokenScore, wholeScore);
    }

    private static int Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var distance = LevenshteinDistance(a, b);
        var max = Math.Max(a.Length, b.Length);
        var score = (int)Math.Round(100.0 * (1.0 - (double)distance / max));
        return score >= 58 ? score : 0;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    public static string BuildSearchText(StudentContact contact)
    {
        return NormalizeForSearch(string.Join(' ', new[]
        {
            contact.UserId.ToString(CultureInfo.InvariantCulture),
            contact.Username,
            contact.FirstName,
            contact.LastName,
            contact.FullName,
            contact.Note
        }.Where(x => !string.IsNullOrWhiteSpace(x))));
    }

    public static string NormalizeForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var lower = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        var sb = new StringBuilder(lower.Length);
        var wasSpace = false;

        foreach (var ch in lower.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                wasSpace = false;
            }
            else if (!wasSpace)
            {
                sb.Append(' ');
                wasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static string? BuildFullName(string? firstName, string? lastName)
    {
        var value = string.Join(' ', new[] { firstName, lastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return NullIfBlank(value);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? TrimForStorage(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

public sealed record StudentDirectoryPage(string? Query, IReadOnlyList<StudentContactSearchItem> Items, bool IsSearch);

public sealed record StudentContactSearchItem(StudentContact Contact, StudentAccessState AccessState, int Score);

public sealed record StudentContactCard(StudentContact Contact, StudentAccessState AccessState, ProgressEntry? Progress);

public enum StudentAccessState
{
    NoAccess,
    Expired,
    Temporary,
    Permanent
}
