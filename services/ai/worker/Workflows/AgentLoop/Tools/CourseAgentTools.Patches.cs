using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static partial class CourseAgentTools
{
    public static JsonObject ProposeAssignmentPatchSet(AgentLoopState state, JsonObject args, int maxOperations)
    {
        var operation = Normalize(args["operation"]?.ToString() ?? "rerate_assignments");
        var field = Normalize(args["field"]?.ToString() ?? "rating");
        if (operation.Contains("rating")) field = "rating";
        if (field is not "rating" and not "difficulty" and not "title" and not "description" and not "tags" and not "language" and not "type" and not "isvisible")
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["summary"] = $"Поле '{field}' пока нельзя безопасно редактировать patch set-ом."
            };
        }

        if (state.WorkingMemory["assignmentComplexityReport"] is not JsonObject complexity)
        {
            AnalyzeAssignmentComplexity(state);
            complexity = state.WorkingMemory["assignmentComplexityReport"] as JsonObject ?? new JsonObject();
        }

        var courseId = state.Job.CourseId?.ToString() ?? state.WorkingMemory["courseDigest"]?["selectedCourseId"]?.ToString();
        var items = complexity["items"] as JsonArray ?? new JsonArray();
        var patches = new JsonArray();
        foreach (var node in items.OfType<JsonObject>())
        {
            if (patches.Count >= maxOperations) break;
            var assignmentId = node["assignmentId"]?.ToString();
            if (string.IsNullOrWhiteSpace(assignmentId)) continue;
            var title = node["title"]?.ToString() ?? "Задание";
            var oldRating = GetIntNode(node["oldRating"]);
            var newRating = GetIntNode(node["suggestedRating"]);
            if (field == "rating")
            {
                if (!newRating.HasValue) continue;
                if (oldRating.HasValue && oldRating.Value == newRating.Value) continue;
                patches.Add(BuildAssignmentPatch(
                    courseId,
                    assignmentId,
                    title,
                    field,
                    oldRating.HasValue ? JsonValue.Create(oldRating.Value) : null,
                    JsonValue.Create(newRating.Value),
                    node["reason"]?.ToString() ?? "Рейтинг рассчитан по сложности задания и месту в курсе.",
                    node));
            }
            else if (field == "difficulty")
            {
                var oldDifficulty = GetIntNode(node["oldDifficulty"]);
                var newDifficulty = GetIntNode(node["estimatedDifficulty"]);
                if (!newDifficulty.HasValue) continue;
                if (oldDifficulty.HasValue && oldDifficulty.Value == newDifficulty.Value) continue;
                patches.Add(BuildAssignmentPatch(
                    courseId,
                    assignmentId,
                    title,
                    field,
                    oldDifficulty.HasValue ? JsonValue.Create(oldDifficulty.Value) : null,
                    JsonValue.Create(newDifficulty.Value),
                    node["reason"]?.ToString() ?? "Сложность рассчитана по типу задания, понятиям и тестам.",
                    node));
            }
        }

        var patchSet = new JsonObject
        {
            ["type"] = "course_patch_set",
            ["title"] = field == "rating" ? "Патч курса: перерасчёт рейтингов заданий" : "Патч курса: массовое редактирование заданий",
            ["operation"] = operation,
            ["field"] = field,
            ["courseId"] = courseId,
            ["patchCount"] = patches.Count,
            ["patches"] = patches,
            ["stats"] = new JsonObject
            {
                ["sourceAssignments"] = items.Count,
                ["patches"] = patches.Count,
                ["skippedWithoutId"] = items.OfType<JsonObject>().Count(x => string.IsNullOrWhiteSpace(x["assignmentId"]?.ToString())),
                ["limit"] = maxOperations
            },
            ["summary"] = patches.Count == 0
                ? "Патчи не созданы: по доступным данным нечего менять или у заданий нет id."
                : $"Подготовлено изменений: {patches.Count}. Открой меню патчей, проверь диффы и только затем применяй.",
            ["assistantMessage"] = patches.Count == 0
                ? "Я изучил задания курса, но не нашёл безопасных изменений для patch set. Проверь логи: возможно, у заданий нет id или текущие значения уже совпадают с расчётом."
                : $"Я подготовил patch set на {patches.Count} изменений. Открой меню патчей: там будут диффы как в GitHub, причины и безопасная проверка перед применением."
        };

        state.WorkingMemory["pendingPatchSet"] = patchSet.DeepClone();
        state.FinalMessage = patchSet["assistantMessage"]?.ToString();
        state.ScenarioId = "course_patch_set";
        state.Notes.Add($"Patch set prepared: {patches.Count} patch(es), field={field}.");
        return new JsonObject
        {
            ["ok"] = patches.Count > 0,
            ["summary"] = patchSet["summary"]?.ToString(),
            ["patchSet"] = patchSet.DeepClone()
        };
    }

    public static JsonObject ReviewPatchSet(AgentLoopState state)
    {
        if (state.WorkingMemory["pendingPatchSet"] is not JsonObject patchSet)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["summary"] = "Нет подготовленного patch set для проверки."
            };
        }

        var patches = patchSet["patches"] as JsonArray ?? new JsonArray();
        var issues = new JsonArray();
        foreach (var patch in patches.OfType<JsonObject>())
        {
            if (string.IsNullOrWhiteSpace(patch["assignmentId"]?.ToString())) issues.Add("Патч без assignmentId.");
            if (patch["changes"] is not JsonArray changes || changes.Count == 0) issues.Add($"{patch["title"]}: нет изменений.");
            else
            {
                foreach (var change in changes.OfType<JsonObject>())
                {
                    var field = Normalize(change["field"]?.ToString() ?? string.Empty);
                    if (field is not "rating" and not "difficulty" and not "title" and not "description" and not "tags" and not "language" and not "type" and not "isvisible")
                        issues.Add($"{patch["title"]}: запрещённое поле {field}.");
                }
            }
        }

        var review = new JsonObject
        {
            ["patchCount"] = patches.Count,
            ["issueCount"] = issues.Count,
            ["issues"] = issues,
            ["isGoodEnoughToShow"] = patches.Count > 0 && issues.Count == 0,
            ["summary"] = issues.Count == 0
                ? $"Patch set проверен: {patches.Count} изменений готовы к просмотру в меню патчей."
                : $"Patch set содержит проблемы: {issues.Count}."
        };
        state.WorkingMemory["patchSetReview"] = review.DeepClone();
        state.Notes.Add("Patch set reviewed before finish.");
        return new JsonObject
        {
            ["ok"] = issues.Count == 0 && patches.Count > 0,
            ["summary"] = review["summary"]?.ToString(),
            ["patchSetReview"] = review.DeepClone()
        };
    }

}
