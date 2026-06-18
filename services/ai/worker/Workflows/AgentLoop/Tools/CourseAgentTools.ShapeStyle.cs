using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static partial class CourseAgentTools
{
    private static string ResolveNextWorkflow(AgentLoopState state)
    {
        var scenario = state.WorkingMemory["intent"]?["scenarioId"]?.ToString() ?? state.ScenarioId;
        return scenario switch
        {
            "course_edit" => "delegate_course_edit",
            "course_analysis" or "course_gap_audit" => "delegate_course_audit",
            "polish_assignment_draft" or "draft_revision" => "delegate_polish_assignment",
            _ => LooksLikeDraftRequest(state.Job.UserText) ? "delegate_assignment_draft" : "answer_directly"
        };
    }

    private static JsonObject BuildCourseShape(List<AssignmentSnapshot> assignments)
    {
        var windows = new JsonArray();
        const int size = 8;
        for (var i = 0; i < assignments.Count; i += size)
        {
            var chunk = assignments.Skip(i).Take(size).ToList();
            if (chunk.Count == 0) continue;
            windows.Add(new JsonObject
            {
                ["fromIndex"] = chunk.First().Index,
                ["toIndex"] = chunk.Last().Index,
                ["count"] = chunk.Count,
                ["types"] = ToCountObject(chunk.GroupBy(x => Normalize(x.Type)).ToDictionary(g => g.Key, g => g.Count())),
                ["concepts"] = new JsonArray(chunk.SelectMany(ExtractConcepts).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>())
            });
        }
        return new JsonObject
        {
            ["windowSize"] = size,
            ["windows"] = windows
        };
    }

    private static JsonArray BuildConceptTimeline(List<AssignmentSnapshot> assignments)
    {
        var timeline = new JsonArray();
        foreach (var item in assignments)
        {
            var concepts = ExtractConcepts(item).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            if (concepts.Count == 0) continue;
            timeline.Add(new JsonObject
            {
                ["index"] = item.Index,
                ["title"] = item.Title,
                ["concepts"] = new JsonArray(concepts.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>())
            });
        }
        return timeline;
    }

    private static string BuildCourseMapSummary(List<AssignmentSnapshot> assignments, Dictionary<string, int> byType, int conceptEntries)
    {
        if (assignments.Count == 0)
            return "В payload не найдено явных заданий курса. Агенту нужно попросить пользователя открыть курс или приложить контекст.";
        var typeText = byType.Count == 0 ? "тип не определён" : string.Join(", ", byType.Select(x => $"{x.Key}: {x.Value}"));
        return $"Карта курса построена: {assignments.Count} заданий, типы: {typeText}, concept entries: {conceptEntries}.";
    }

    private static string BuildDescriptionStyleRecommendation(int total, int hasGoal, int hasSteps, int hasInputFormat, int hasBackticks)
    {
        if (total == 0) return "Нет примеров для извлечения стиля.";
        if (hasGoal > total / 3 || hasSteps > total / 3)
            return "Курс похож на обучающий: новые задания лучше писать с целью, разбором и маленькими шагами.";
        if (hasInputFormat > total / 3)
            return "Курс похож на задачник: новые задания должны иметь аккуратные форматы ввода/вывода и тесты.";
        if (hasBackticks > total / 3)
            return "В курсе часто используется inline-code: сохраняй backticks для кода и терминов.";
        return "Стиль смешанный: сохраняй student-facing формулировки и не добавляй служебную metadata в описание.";
    }

    private static JsonObject BuildNumberStats(List<int> values)
    {
        if (values.Count == 0)
            return new JsonObject { ["available"] = false };
        return new JsonObject
        {
            ["available"] = true,
            ["min"] = values.Min(),
            ["max"] = values.Max(),
            ["average"] = System.Math.Round(values.Average(), 2)
        };
    }

    private static JsonObject Finding(string severity, string title, string assignmentTitle, string evidence, string recommendation) => new()
    {
        ["severity"] = severity,
        ["title"] = title,
        ["assignmentTitle"] = assignmentTitle,
        ["evidence"] = evidence,
        ["recommendation"] = recommendation
    };


    private static JsonObject BuildAssignmentPatch(string? courseId, string assignmentId, string title, string field, JsonNode? oldValue, JsonNode? newValue, string reason, JsonObject sourceAnalysis)
    {
        var fileName = $"assignments/{SafeFilePart(title)}.json";
        var change = new JsonObject
        {
            ["field"] = field,
            ["oldValue"] = oldValue?.DeepClone(),
            ["newValue"] = newValue?.DeepClone(),
            ["reason"] = reason
        };
        var patch = new JsonObject
        {
            ["operation"] = "update_assignment",
            ["courseId"] = courseId,
            ["assignmentId"] = assignmentId,
            ["title"] = title,
            ["changeCount"] = 1,
            ["changes"] = new JsonArray(change),
            ["analysis"] = sourceAnalysis.DeepClone(),
            ["diff"] = new JsonObject
            {
                ["filePath"] = fileName,
                ["oldPath"] = $"a/{fileName}",
                ["newPath"] = $"b/{fileName}",
                ["hunks"] = new JsonArray(new JsonObject
                {
                    ["header"] = $"@@ assignment.{field} @@",
                    ["lines"] = new JsonArray(
                        new JsonObject { ["type"] = "context", ["text"] = $"// {title}" },
                        new JsonObject { ["type"] = "removed", ["text"] = $"\"{field}\": {JsonLiteral(oldValue)}" },
                        new JsonObject { ["type"] = "added", ["text"] = $"\"{field}\": {JsonLiteral(newValue)}" })
                })
            }
        };
        return patch;
    }

    private static string BuildComplexityReason(AssignmentSnapshot item, List<string> concepts, int estimatedDifficulty, int score)
    {
        var bits = new List<string>
        {
            $"оценка сложности: {estimatedDifficulty}",
            $"score={score}",
            $"позиция в курсе: {item.Index}"
        };
        if (item.Difficulty.HasValue) bits.Add($"текущая difficulty={item.Difficulty.Value}");
        if (concepts.Count > 0) bits.Add("понятия: " + string.Join(", ", concepts.Take(6)));
        if (item.PublicTestCount + item.HiddenTestCount > 0) bits.Add($"тестов: {item.PublicTestCount + item.HiddenTestCount}, hidden: {item.HiddenTestCount}");
        if (!item.HasReferenceSolution && item.Type.Contains("code", StringComparison.OrdinalIgnoreCase)) bits.Add("нет эталонного решения — уверенность ниже");
        return string.Join("; ", bits);
    }

    private static int ConceptWeight(string concept)
    {
        var c = Normalize(concept);
        if (c.Contains("ооп") || c.Contains("класс") || c.Contains("api") || c.Contains("sql") || c.Contains("react")) return 18;
        if (c.Contains("массив") || c.Contains("строк") || c.Contains("функц") || c.Contains("цикл")) return 12;
        if (c.Contains("услов") || c.Contains("if") || c.Contains("scanf") || c.Contains("printf")) return 8;
        return 4;
    }

    private static int RoundToNearest5(int value) => (int)(System.Math.Round(value / 5.0) * 5);

    private static int? GetIntNode(JsonNode? node)
    {
        if (node is null) return null;
        if (int.TryParse(node.ToString(), out var value)) return value;
        if (double.TryParse(node.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number)) return (int)System.Math.Round(number);
        return null;
    }

    private static string SafeFilePart(string value)
    {
        var text = string.Join('-', (value ?? "assignment").Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(text) ? "assignment" : text.Length <= 80 ? text : text[..80];
    }

    private static string JsonLiteral(JsonNode? node)
    {
        if (node is null) return "null";
        var text = node.ToJsonString();
        return string.IsNullOrWhiteSpace(text) ? "null" : text;
    }

    private static JsonObject ToCountObject(Dictionary<string, int> data)
    {
        var obj = new JsonObject();
        foreach (var pair in data.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            obj[pair.Key] = pair.Value;
        return obj;
    }

    private static JsonObject ToSnapshotJson(AssignmentSnapshot item, int? score = null)
    {
        var obj = new JsonObject
        {
            ["index"] = item.Index,
            ["source"] = item.Source,
            ["id"] = item.Id,
            ["title"] = item.Title,
            ["type"] = item.Type,
            ["language"] = item.Language,
            ["difficulty"] = item.Difficulty,
            ["rating"] = item.Rating,
            ["tags"] = new JsonArray(item.Tags.Take(12).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
            ["concepts"] = new JsonArray(ExtractConcepts(item).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
            ["publicTests"] = item.PublicTestCount,
            ["hiddenTests"] = item.HiddenTestCount,
            ["descriptionPreview"] = Trim(item.Description, 360)
        };
        if (score.HasValue) obj["score"] = score.Value;
        return obj;
    }

}
