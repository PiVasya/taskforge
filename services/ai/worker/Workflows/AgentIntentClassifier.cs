using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows;

public sealed record AgentIntent(
    string ScenarioId,
    int Confidence,
    string Reason,
    string ExecutionMode = "single",
    string? SecondaryScenarioId = null,
    int? RequestedCount = null,
    string? TargetConcept = null,
    string? RequestedStyle = null)
{
    public JsonObject ToJsonObject() => new()
    {
        ["scenarioId"] = ScenarioId,
        ["confidence"] = Confidence,
        ["reason"] = Reason,
        ["executionMode"] = ExecutionMode,
        ["secondaryScenarioId"] = SecondaryScenarioId,
        ["requestedCount"] = RequestedCount,
        ["targetConcept"] = TargetConcept,
        ["requestedStyle"] = RequestedStyle
    };

    public bool IsDraftScenario => ScenarioId is "guided_ladder" or "style_matched_tasks" or "bridge_tasks" or "draft_revision";
    public bool IsCourseAuditScenario => ScenarioId is "course_analysis" or "course_gap_audit";
    public bool IsCourseEditScenario => ScenarioId == "course_edit";
}

public static class AgentIntentClassifier
{
    private sealed record Rule(string ScenarioId, string[] MustContainAny, string[] MustNotContainAny, int Priority, string Reason);

    private static readonly Regex[] CountPatterns =
    {
        new(@"(?:сделай|создай|дай|нужно|надо|подготовь|накидай)\s+(\d{1,3})\s+(?:задач|задани|шаг|чернов)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"(\d{1,3})\s+(?:задач|задани|шаг|чернов)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)
    };

    private static readonly (string Concept, string[] Aliases)[] ConceptAliases =
    {
        ("if", new[] { "if", "иф", "услов", "ветвлен", "ветвлени", "если", "else" }),
        ("input", new[] { "input", "ввод", "считыван", "прочитать", "ввести" }),
        ("variables", new[] { "переменн", "variable", "присваиван" }),
        ("loops", new[] { "цикл", "for", "while", "повтор" }),
        ("arrays", new[] { "массив", "список", "array", "list" }),
        ("functions", new[] { "функци", "def", "метод" }),
        ("strings", new[] { "строк", "string" }),
        ("math", new[] { "математ", "формул", "арифмет" })
    };

    private static readonly Rule[] ScenarioRules =
    {
        new(
            "course_edit",
            new[] { "редактир", "измени задания", "поменяй задания", "подгони", "подогнать", "единый стиль", "один стиль", "перестав", "порядок задан", "расставь рейтинг", "рейтинг", "нормализуй", "выровняй" },
            new[] { "только объясни", "не меняй" },
            99,
            "Пользователь просит изменить существующие задания курса: стиль, порядок, рейтинг или содержимое."),
        new(
            "course_analysis",
            new[] { "анализ", "изучи", "изучить", "разбери", "посмотри", "пойми", "проверь курс", "структур", "карта курса" },
            new[] { "не анализ" },
            96,
            "Пользователь просит изучить/проанализировать курс или набор курсов."),
        new(
            "course_gap_audit",
            new[] { "пробел", "скач", "не хватает", "слаб", "аудит", "застр", "переход", "перед", "найди места", "места куда", "куда бы ты вставил", "куда бы ты выставил", "куда вставить", "куда добавить", "где вставить", "где добавить", "обучающие задачи", "обучающие задан" },
            new[] { "не анализ" },
            95,
            "Пользователь просит найти пробелы, резкие скачки сложности или места для вставки обучающих задач."),
        new(
            "guided_ladder",
            new[] { "лесен", "пошаг", "маленьк", "с нуля", "микро", "ступен", "обучал" },
            new[] { "одну сложную", "без разж" },
            90,
            "Пользователь просит обучающую последовательность или микро-шаги."),
        new(
            "bridge_tasks",
            new[] { "мостик", "мост", "между заданиями", "между темами", "подвести" },
            Array.Empty<string>(),
            84,
            "Пользователь просит мостик между заданиями или темами."),
        new(
            "style_matched_tasks",
            new[] { "в стиле курса", "как в курсе", "похож", "как текущ", "ещё задач", "создай задач", "сделай задач", "придумай задач", "сгенер" },
            new[] { "другим стилем" },
            82,
            "Пользователь просит создать задачи/черновики."),
        new(
            "draft_revision",
            new[] { "исправ", "передел", "упрост", "сложнее", "мягче", "поправ", "не так" },
            Array.Empty<string>(),
            80,
            "Пользователь просит правку существующего результата.")
    };

    public static AgentIntent Select(ClaimedAgentJob job)
    {
        var text = job.UserText ?? string.Empty;
        var lowered = text.ToLowerInvariant();
        var requestedCount = RequestedCount(lowered);
        var targetConcept = TargetConcept(lowered);
        var requestedStyle = HasAny(lowered, "лесен", "пошаг", "микро", "ступен") ? "guided_learning_path" : null;

        var wantsLadder = HasAny(lowered, "лесен", "пошаг", "маленьк", "с нуля", "микро", "ступен", "обучал");
        var wantsGap = HasAny(lowered, "пробел", "скач", "не хватает", "слаб", "застр", "аудит", "переход", "перед if", "перед иф", "к ним");
        var wantsAnalysis = HasAny(lowered, "анализ", "изучи курс", "изучить курс", "разбери курс", "посмотри курс", "пойми курс", "проверь курс", "структур", "карта курса");
        var generationVerbs = HasAny(lowered, "создай", "сделай", "придумай", "сгенер", "подготовь", "дай ", "накидай");
        var taskWords = HasAny(lowered, "задач", "задани", "черновик", "упражнен");
        var wantsGeneration = generationVerbs && taskWords;
        var wantsRevision = HasAny(lowered,
            "исправ", "передел", "упрост", "сложнее", "мягче", "не так", "поправ",
            "эти же", "те же", "то же", "так же", "эти самые", "прям лесен", "каждым шагом",
            "как задача 1", "как задание 1", "пример задача 1", "пример задание 1");

        if (HasAny(lowered, "в какой курс", "какой курс", "куда сохран", "куда сохрани", "где сохран", "сохранены задач", "сохранены черновик", "после чего сохран", "куда они"))
            return NewIntent("free_chat", 92, "Пользователь спрашивает статус/место сохранения уже созданных черновиков, а не просит генерировать новые задачи.", requestedCount, targetConcept, requestedStyle);

        if (HasAny(lowered, "редактир", "существующ", "подгони", "подогнать", "единый стиль", "один стиль", "перестав", "порядок", "расставь рейтинг", "рейтинг", "нормализуй", "выровняй"))
            return NewIntent("course_edit", 98, "Пользователь просит применить правки к существующим заданиям курса.", requestedCount, targetConcept, requestedStyle);

        if (wantsRevision && HasCurrentDraft(job.Payload))
            return NewIntent("draft_revision", 97, "Пользователь правит уже созданный AI-черновик; нужно сохранить контекст и стиль предыдущего результата.", requestedCount, targetConcept, requestedStyle);

        if (HasAny(lowered, "найди места", "места куда", "куда бы ты вставил", "куда бы ты выставил", "куда вставить", "куда добавить", "где вставить", "где добавить", "обучающие задачи", "обучающие задан"))
            return NewIntent("course_gap_audit", 96, "Пользователь просит просканировать курс и найти универсальные точки для вставки обучающих задач.", requestedCount, targetConcept, requestedStyle);

        if (wantsAnalysis && wantsGap && wantsLadder)
            return NewIntent("course_analysis", 98, "Нужно сначала изучить курсы/курс, затем найти пробелы; обучающую последовательность можно запросить следующим сообщением или отдельным шагом.", requestedCount, targetConcept, requestedStyle, "chain", "course_gap_audit");

        if (wantsGap && wantsLadder)
            return NewIntent("course_gap_audit", 96, "Пользователь просит найти пробел и сразу подготовить обучающую последовательность.", requestedCount ?? 5, targetConcept, requestedStyle, "chain", "guided_ladder");

        if (wantsAnalysis && (wantsGap || targetConcept is not null))
            return NewIntent("course_analysis", 94, "Пользователь просит изучить курс/курсы и проверить переход к целевой теме.", requestedCount, targetConcept, requestedStyle, "chain", "course_gap_audit");

        if (wantsGeneration && wantsLadder)
            return NewIntent("guided_ladder", 94, "Пользователь просит создать обучающую последовательность.", requestedCount, targetConcept, requestedStyle);

        if (wantsGeneration)
            return NewIntent("style_matched_tasks", 90, "Пользователь просит создать задачи; генерация будет выполнена реальным LLM-вызовом.", requestedCount, targetConcept, requestedStyle);

        var bestRule = ScenarioRules
            .Where(rule => HasAny(lowered, rule.MustContainAny) && !HasAny(lowered, rule.MustNotContainAny))
            .OrderByDescending(rule => rule.Priority)
            .FirstOrDefault();

        if (bestRule is not null)
            return NewIntent(bestRule.ScenarioId, bestRule.Priority, bestRule.Reason, requestedCount, targetConcept, requestedStyle);

        return NewIntent("free_chat", 70, "Явный сценарий не найден; запускается обычный AI-чат с доступным контекстом курсов.", requestedCount, targetConcept, requestedStyle);
    }

    private static AgentIntent NewIntent(string scenarioId, int confidence, string reason, int? requestedCount, string? targetConcept, string? requestedStyle, string executionMode = "single", string? secondaryScenarioId = null)
        => new(scenarioId, confidence, reason, executionMode, secondaryScenarioId, requestedCount, targetConcept, requestedStyle);

    private static bool HasAny(string text, params string[] values) => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool HasAny(string text, IEnumerable<string> values) => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static int? RequestedCount(string text)
    {
        foreach (var pattern in CountPatterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            if (int.TryParse(match.Groups[1].Value, out var value) && value is >= 1 and <= 100)
                return value;
        }
        return null;
    }

    private static string? TargetConcept(string text)
    {
        foreach (var (concept, aliases) in ConceptAliases)
        {
            if (HasAny(text, aliases)) return concept;
        }
        return null;
    }

    private static bool HasCurrentDraft(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;

        var memory = payload.GetPropertyOrDefault("memory");
        if (memory.ValueKind == JsonValueKind.Object)
        {
            var current = memory.GetPropertyOrDefault("currentDraftBlueprint");
            if (current.ValueKind == JsonValueKind.Object && current.EnumerateObject().Any())
                return true;
        }

        var recentDrafts = payload.GetPropertyOrDefault("recentDrafts");
        if (recentDrafts.ValueKind == JsonValueKind.Undefined)
            recentDrafts = payload.GetPropertyOrDefault("recent_drafts");
        return recentDrafts.ValueKind == JsonValueKind.Array && recentDrafts.GetArrayLength() > 0;
    }
}
