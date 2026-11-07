using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Testing;

// The author may replace/re-import a test while a learner has an open attempt.
// A versioned snapshot in the existing jsonb OrderJson column freezes the exact
// question content, answer keys and scoring settings without changing the DB schema.
internal static class TestAttemptQuestionSnapshot
{
    internal static string Create(TaskSpec spec, IReadOnlyList<Guid> order)
    {
        return new JsonObject
        {
            ["version"] = 1,
            ["order"] = JsonSerializer.SerializeToNode(order, JsonOptions()),
            ["spec"] = TaskSpecToJsonObject(spec)
        }.ToJsonString(JsonOptions());
    }

    internal static bool TryUpgradeLegacy(TaskAttempt attempt, TaskSpec previous)
    {
        if (attempt.OrderJson?.TrimStart().StartsWith('{') == true) return false;
        if (!TryResolve(attempt, previous, out _, out var order)) return false;
        if (order.Count == 0) return false;
        var indexed = previous.Questions.ToDictionary(q => q.Id);
        var frozen = new TaskSpec(previous.Settings, order.Select(id => indexed[id]).ToList());
        attempt.OrderJson = Create(frozen, order);
        attempt.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    internal static bool TryResolve(TaskAttempt attempt, TaskSpec current, out TaskSpec resolved, out List<Guid> order)
    {
        resolved = current;
        order = [];
        var raw = attempt.OrderJson?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Compatibility with attempts created before order tracking existed.
            order = current.Questions.Select(q => q.Id).ToList();
            return true;
        }

        try
        {
            var node = JsonNode.Parse(raw);
            if (node is JsonArray legacyOrder)
            {
                order = JsonSerializer.Deserialize<List<Guid>>(legacyOrder.ToJsonString(JsonOptions()), JsonOptions()) ?? [];
                // Legacy attempts contain only IDs. If an import removed any of them,
                // explicitly reject instead of silently scoring fewer/zero questions.
                var available = current.Questions.Select(q => q.Id).ToHashSet();
                return order.Count > 0 && order.Distinct().Count() == order.Count && order.All(available.Contains);
            }

            if (node is not JsonObject snapshot || snapshot["version"]?.GetValue<int>() != 1
                || snapshot["order"] is not JsonArray savedOrder
                || snapshot["spec"] is not JsonObject savedSpec
                || savedSpec["questions"] is not JsonArray savedQuestions)
                return false;

            order = JsonSerializer.Deserialize<List<Guid>>(savedOrder.ToJsonString(JsonOptions()), JsonOptions()) ?? [];
            if (order.Count == 0 || order.Distinct().Count() != order.Count || savedQuestions.Count != order.Count)
                return false;

            var ids = new HashSet<Guid>();
            foreach (var question in savedQuestions)
            {
                if (question is not JsonObject item || !Guid.TryParse(item["id"]?.ToString(), out var id)
                    || id == Guid.Empty || !ids.Add(id))
                    return false;
            }
            if (!order.All(ids.Contains)) return false;
            resolved = ParseTaskSpec(savedSpec);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FormatException) { return false; }
    }
}
