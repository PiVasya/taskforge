using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static partial class CourseAgentTools
{
    private static readonly string[] ConceptKeywords =
    {
        "ввод", "вывод", "cout", "cin", "printf", "scanf", "переменн", "int", "float", "double", "char",
        "арифмет", "деление", "остат", "услов", "if", "else", "switch", "цикл", "for", "while", "массив",
        "список", "коллекц", "list", "array", "строк", "string", "функц", "метод", "класс", "объект",
        "ооп", "наслед", "интерфейс", "исключ", "файл", "json", "api", "http", "sql", "react", "redux",
        "html", "css", "тест", "math", "формат", "парс", "regex", "linq"
    };

    private sealed record AssignmentSnapshot(
        int Index,
        string Source,
        string? Id,
        string Title,
        string Type,
        string? Language,
        int? Difficulty,
        int? Rating,
        List<string> Tags,
        string Description,
        int PublicTestCount,
        int HiddenTestCount,
        bool HasStarterCode,
        bool HasReferenceSolution,
        JsonObject Raw);

    public static JsonObject MapCourseStructure(AgentLoopState state)
    {
        var assignments = GetAssignments(state);
        var byType = assignments.GroupBy(x => Normalize(x.Type)).ToDictionary(g => g.Key, g => g.Count());
        var byLanguage = assignments.Where(x => !string.IsNullOrWhiteSpace(x.Language)).GroupBy(x => Normalize(x.Language!)).ToDictionary(g => g.Key, g => g.Count());
        var concepts = BuildConceptTimeline(assignments);
        var difficultyValues = assignments.Where(x => x.Difficulty.HasValue).Select(x => x.Difficulty!.Value).ToList();
        var ratingValues = assignments.Where(x => x.Rating.HasValue).Select(x => x.Rating!.Value).ToList();

        var map = new JsonObject
        {
            ["assignmentCount"] = assignments.Count,
            ["sources"] = ToCountObject(assignments.GroupBy(x => x.Source).ToDictionary(g => g.Key, g => g.Count())),
            ["types"] = ToCountObject(byType),
            ["languages"] = ToCountObject(byLanguage),
            ["difficulty"] = BuildNumberStats(difficultyValues),
            ["rating"] = BuildNumberStats(ratingValues),
            ["firstAssignments"] = new JsonArray(assignments.Take(8).Select(x => ToSnapshotJson(x)).ToArray<JsonNode?>()),
            ["lastAssignments"] = new JsonArray(assignments.TakeLast(8).Select(x => ToSnapshotJson(x)).ToArray<JsonNode?>()),
            ["conceptTimeline"] = concepts,
            ["courseShape"] = BuildCourseShape(assignments),
            ["summary"] = BuildCourseMapSummary(assignments, byType, concepts.Count)
        };

        state.WorkingMemory["courseMap"] = map.DeepClone();
        state.Notes.Add($"Course structure mapped: {assignments.Count} assignment snapshots, {concepts.Count} concept entries.");
        return new JsonObject
        {
            ["ok"] = true,
            ["assignmentCount"] = assignments.Count,
            ["summary"] = map["summary"]?.ToString(),
            ["courseMap"] = map.DeepClone()
        };
    }

    public static JsonObject ExtractCourseStyle(AgentLoopState state)
    {
        var assignments = GetAssignments(state);
        var titleNumbered = assignments.Count(x => StartsWithNumber(x.Title));
        var hasGoal = assignments.Count(x => ContainsAny(x.Description, "Цель задания", "Цель:", "Цель"));
        var hasSteps = assignments.Count(x => ContainsAny(x.Description, "Пошагово", "Следуй шагам", "Шаг"));
        var hasInputFormat = assignments.Count(x => ContainsAny(x.Description, "Формат ввода", "Ввод:"));
        var hasOutputFormat = assignments.Count(x => ContainsAny(x.Description, "Формат вывода", "Вывод:"));
        var hasBackticks = assignments.Count(x => x.Description.Contains('`'));
        var shortDescriptions = assignments.Count(x => x.Description.Trim().Length is > 0 and < 160);
        var publicCounts = assignments.Select(x => x.PublicTestCount).Where(x => x > 0).ToList();
        var hiddenCounts = assignments.Select(x => x.HiddenTestCount).Where(x => x > 0).ToList();
        var commonTags = assignments.SelectMany(x => x.Tags).Where(x => !string.IsNullOrWhiteSpace(x)).GroupBy(Normalize).OrderByDescending(g => g.Count()).Take(20).ToDictionary(g => g.Key, g => g.Count());

        var style = new JsonObject
        {
            ["assignmentCount"] = assignments.Count,
            ["titleStyle"] = new JsonObject
            {
                ["numberedTitles"] = titleNumbered,
                ["numberedRatio"] = Ratio(titleNumbered, assignments.Count),
                ["examples"] = new JsonArray(assignments.Take(8).Select(x => JsonValue.Create(x.Title)).ToArray<JsonNode?>())
            },
            ["descriptionStyle"] = new JsonObject
            {
                ["usesGoalBlocks"] = hasGoal,
                ["usesStepByStepBlocks"] = hasSteps,
                ["usesInputFormatSections"] = hasInputFormat,
                ["usesOutputFormatSections"] = hasOutputFormat,
                ["usesInlineCodeBackticks"] = hasBackticks,
                ["shortDescriptions"] = shortDescriptions,
                ["studentFacingRecommendation"] = BuildDescriptionStyleRecommendation(assignments.Count, hasGoal, hasSteps, hasInputFormat, hasBackticks)
            },
            ["testingStyle"] = new JsonObject
            {
                ["averagePublicTests"] = Average(publicCounts),
                ["averageHiddenTests"] = Average(hiddenCounts),
                ["assignmentsWithoutTests"] = assignments.Count(x => x.PublicTestCount + x.HiddenTestCount == 0),
                ["assignmentsWithoutHiddenTests"] = assignments.Count(x => x.PublicTestCount + x.HiddenTestCount > 0 && x.HiddenTestCount == 0)
            },
            ["commonTags"] = ToCountObject(commonTags),
            ["languageProfile"] = ToCountObject(assignments.Where(x => !string.IsNullOrWhiteSpace(x.Language)).GroupBy(x => Normalize(x.Language!)).ToDictionary(g => g.Key, g => g.Count())),
            ["summary"] = "Стиль курса извлечён из существующих заданий: названия, описания, тесты, теги и язык. Используй это как эталон при генерации новых задач."
        };

        state.WorkingMemory["courseStyleProfile"] = style.DeepClone();
        state.Notes.Add("Course style profile extracted and saved to working memory.");
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = "Стиль существующих заданий сохранён в памяти агента.",
            ["courseStyleProfile"] = style.DeepClone()
        };
    }

}
