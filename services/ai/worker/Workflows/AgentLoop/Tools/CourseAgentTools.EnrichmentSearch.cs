using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static partial class CourseAgentTools
{
    public static JsonObject PlanCourseEnrichment(AgentLoopState state)
    {
        var intent = state.WorkingMemory["intent"]?.DeepClone();
        var courseMap = state.WorkingMemory["courseMap"]?.DeepClone();
        var style = state.WorkingMemory["courseStyleProfile"]?.DeepClone();
        var gaps = state.WorkingMemory["courseGapReport"]?.DeepClone();
        var assignments = GetAssignments(state);
        var userText = state.Job.UserText;

        var brief = new JsonObject
        {
            ["userRequest"] = userText,
            ["intent"] = intent,
            ["courseMap"] = courseMap,
            ["courseStyleProfile"] = style,
            ["courseGapReport"] = gaps,
            ["generationRules"] = new JsonArray(
                "Сохраняй стиль существующего курса: похожие названия, уровень подробности, формат описаний, язык и теги.",
                "Не добавляй админский или служебный текст в видимое описание задания.",
                "Если задача обучающая, объясняй один маленький новый навык и давай пошаговые действия.",
                "Если задача проверочная, формулируй кратко и без подсказок.",
                "starterCode не должен содержать готовое решение.",
                "Для code-test нужны рабочий referenceSolution, public tests и hidden tests.",
                "Если курс уже имеет близкий стиль, новые задачи должны выглядеть как естественное продолжение, а не как отдельный AI-блок."),
            ["recommendedContextAnchors"] = new JsonArray(assignments.TakeLast(6).Select(x => ToSnapshotJson(x)).ToArray<JsonNode?>()),
            ["nextBestWorkflow"] = ResolveNextWorkflow(state),
            ["summary"] = "Подготовлен единый brief для следующего workflow: запрос, память чата, карта курса, стиль, пробелы и правила качества."
        };

        state.WorkingMemory["courseEnrichmentBrief"] = brief.DeepClone();
        state.Notes.Add("Course enrichment brief prepared for downstream workflow.");
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = brief["summary"]?.ToString(),
            ["nextBestWorkflow"] = brief["nextBestWorkflow"]?.ToString(),
            ["courseEnrichmentBrief"] = brief.DeepClone()
        };
    }

    public static JsonObject SearchCourse(AgentLoopState state, JsonObject args)
    {
        var query = args["query"]?.ToString() ?? state.Job.UserText;
        var concepts = ExtractConcepts(query).ToList();
        var q = Normalize(query);
        var assignments = GetAssignments(state);
        var matches = assignments
            .Select(x => new { Assignment = x, Score = SearchScore(x, q, concepts) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Assignment.Index)
            .Take(20)
            .Select(x => ToSnapshotJson(x.Assignment, x.Score))
            .ToArray<JsonNode?>();

        var result = new JsonObject
        {
            ["query"] = query,
            ["concepts"] = new JsonArray(concepts.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
            ["matchCount"] = matches.Length,
            ["matches"] = new JsonArray(matches),
            ["summary"] = matches.Length == 0
                ? "По доступному контексту курса совпадений не найдено."
                : $"Найдено совпадений в курсе: {matches.Length}."
        };
        state.WorkingMemory["lastCourseSearch"] = result.DeepClone();
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = result["summary"]?.ToString(),
            ["search"] = result.DeepClone()
        };
    }

    public static JsonObject ReviewDelegatedResult(AgentLoopState state)
    {
        if (state.DelegatedResult is null)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["summary"] = "Пока нет результата workflow, который можно проверить."
            };
        }

        var issues = new JsonArray();
        var artifacts = state.DelegatedResult.Artifacts;
        if (artifacts.Count == 0)
            issues.Add("Workflow не вернул материалы/artifacts. Для генерации задач это подозрительно.");

        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Title))
                issues.Add($"Материал типа {artifact.Type} без названия.");
            if (artifact.Data is JsonObject obj)
            {
                var assignmentType = obj["assignmentType"]?.ToString() ?? obj["type"]?.ToString();
                if (string.Equals(assignmentType, "code-test", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(obj["referenceSolution"]?.ToString()))
                        issues.Add($"{artifact.Title}: нет referenceSolution.");
                    var publicCount = CountArray(obj["publicTests"]) + CountArray(obj["testCases"]);
                    var hiddenCount = CountArray(obj["hiddenTests"]);
                    if (publicCount == 0)
                        issues.Add($"{artifact.Title}: нет public tests.");
                    if (hiddenCount == 0)
                        issues.Add($"{artifact.Title}: нет hidden tests.");
                }
            }
        }

        var review = new JsonObject
        {
            ["artifactCount"] = artifacts.Count,
            ["artifactTypes"] = new JsonArray(artifacts.Select(x => JsonValue.Create(x.Type)).ToArray<JsonNode?>()),
            ["issues"] = issues,
            ["isGoodEnoughToFinish"] = issues.Count == 0,
            ["summary"] = issues.Count == 0
                ? "Результат workflow выглядит пригодным для завершения run-а."
                : $"У результата workflow есть замечания: {issues.Count}."
        };
        state.WorkingMemory["delegatedResultReview"] = review.DeepClone();
        state.Notes.Add("Delegated workflow result reviewed before finish.");
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = review["summary"]?.ToString(),
            ["review"] = review.DeepClone()
        };
    }

}
