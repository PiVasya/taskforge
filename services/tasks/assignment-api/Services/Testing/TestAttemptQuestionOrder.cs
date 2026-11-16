using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Testing;

// Attempts retain only a question order, never a copy of the test's content or answer keys.
// All question data and grading rules are read from the current assignment spec.
internal static class TestAttemptQuestionOrder
{
    internal static string Create(IEnumerable<Guid> order)
        => JsonSerializer.Serialize(order, JsonOptions());

    internal static List<Guid> Resolve(TaskAttempt attempt, TaskSpec current)
    {
        var ids = current.Questions.Select(question => question.Id).Distinct().ToList();
        if (!current.Settings.ShuffleQuestions)
            return ids;

        var persisted = ReadOrder(attempt.OrderJson);
        var allowed = ids.ToHashSet();
        var seen = new HashSet<Guid>();
        var order = persisted.Where(id => allowed.Contains(id) && seen.Add(id)).ToList();
        // Deleted questions vanish; newly added questions are included in the same attempt.
        // The existing order of surviving questions remains stable for shuffled tests.
        order.AddRange(OrderedIds(ids.Where(id => !seen.Contains(id)), true, attempt.Id));
        return order;
    }

    internal static bool Refresh(TaskAttempt attempt, TaskSpec current)
    {
        var canonical = Create(Resolve(attempt, current));
        if (string.Equals(attempt.OrderJson, canonical, StringComparison.Ordinal)) return false;
        attempt.OrderJson = canonical;
        attempt.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private static List<Guid> ReadOrder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            // A historic object may include a full `spec`. Never read that content.
            var node = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("order", out var saved)
                ? saved : root;
            return node.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<Guid>>(node.GetRawText(), JsonOptions()) ?? []
                : [];
        }
        catch (JsonException) { return []; }
        catch (NotSupportedException) { return []; }
    }
}
