using System.Text.RegularExpressions;
using taskforge.Data.Models.DTO.AI;

namespace taskforge.Services.AI;

internal sealed record AiLadderSlotContract(
    int Index,
    int TotalCount,
    string StepRole,
    string ComplexityBand,
    int NewIdeaBudget,
    bool RequiresFriendlyIntro,
    bool RequiresStepsBlock,
    bool RequiresRunOutcome,
    IReadOnlyList<string> RequiredSections,
    IReadOnlyList<string> AntiPatterns);

internal static class AiLadderScenarioSupport
{
    private static readonly Regex[] ConceptPatterns =
    {
        new(@"(?:по|на)\s+теме\s+([^\n\r\.,;:!?]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"тему\s+([^\n\r\.,;:!?]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"освоени[ея]\s+([^\n\r\.,;:!?]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"использовать\s+([^\n\r\.,;:!?]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"пользоваться\s+([^\n\r\.,;:!?]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"работ[аы]\s+с\s+([^\n\r\.,;:!?]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"уч[иа]т[ья]?\s+([^\n\r\.,;:!?]{2,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    };

    private static readonly string[] GenericNoise =
    {
        "с нуля", "пошагово", "по шагам", "для курса", "маленькими шагами", "несколько задач", "несколько программ",
        "серия задач", "серия программ", "лесенка", "задач", "программ", "программа", "задачи", "тема", "темы"
    };

    private static readonly string[] SlotDescriptors =
    {
        "самый первый микрошаг: одно понятное действие и мгновенный видимый результат",
        "тот же навык, но уже с чуть более живым использованием или с пользовательским вводом",
        "первое аккуратное усложнение: добавляется ровно одна новая маленькая идея поверх базы",
        "спокойная комбинированная практика: два связанных действия, но без резкого скачка сложности",
        "чуть более взрослая практика на тот же навык в понятной реальной формулировке",
        "итоговое закрепление всей лесенки без смешивания слишком многих новых идей"
    };

    public static string ExtractLearningConcept(AiFoundryChatMemoryDto? memory, string? prompt, string? sourceText)
    {
        var haystack = string.Join(" ", new[]
        {
            prompt,
            sourceText,
            memory?.LatestExplicitInstruction,
            memory?.LatestTeachingScript,
            memory?.AgentState?.UserIntentSummary,
            memory?.AgentState?.ObjectiveSummary,
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        if (string.IsNullOrWhiteSpace(haystack))
            return string.Empty;

        foreach (var pattern in ConceptPatterns)
        {
            var match = pattern.Match(haystack);
            if (!match.Success)
                continue;
            var candidate = NormalizeConcept(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return string.Empty;
    }

    public static string BuildStyleContract(string? concept, int count)
    {
        var conceptLine = string.IsNullOrWhiteSpace(concept)
            ? "Сохраняй точную тему пользователя и не подменяй её соседней идеей."
            : $"Точная учебная цель серии: «{concept}». Не подменяй её соседней темой и не уезжай в другую конструкцию.";

        return $@"TF_LADDER_STYLE
STYLE_KIND=friendly_walkthrough
SERIES_COUNT={count}
CONCEPT={(string.IsNullOrWhiteSpace(concept) ? "<same-as-user-request>" : concept)}
REQUIRED_SECTIONS=title|intro|steps|run-outcome
REQUIRED_PATTERNS=short-friendly-intro|follow-the-steps|numbered-steps|parentheses-explanations|gentle-finish
ANTI_PATTERNS=dry-olympiad-tone|bare-task-statement|jump-in-difficulty|references-to-other-slots|multiple-new-ideas
END_TF_LADDER_STYLE

Стиль лесенки должен быть как у очень понятного первого учебного задания.
- Это не олимпиадная формулировка и не сухой code-test.
- Каждая задача должна ощущаться как маленькое обучение, а не как голая проверка.
- Сначала короткое дружелюбное вступление на 1-3 предложения.
- Затем отдельный блок «Следуй шагам:».
- Внутри — нумерованные шаги с конкретными действиями.
- После важных шагов давай короткие пояснения в скобках простым языком.
- Завершай фразой о том, что именно ученик увидит после запуска.
- Каждая задача должна быть самостоятельной и завершённой.
- В серии из {count} задач рост сложности должен быть очень плавным.
- Не начинай описание сухим шаблоном «Напиши программу...» без живого входа.
- Не смешивай несколько новых идей в одной задаче.
{conceptLine}";
    }

    public static AiLadderSlotContract BuildSlotContract(int index, int totalCount)
    {
        index = Math.Clamp(index, 1, Math.Max(1, totalCount));
        totalCount = Math.Max(1, totalCount);
        var descriptor = DescribeSlotRole(index, totalCount);
        var band = index switch
        {
            1 => "very-gentle",
            2 => "gentle",
            3 => "light-growth",
            4 => "combined-practice",
            5 => "confident-practice",
            _ => "wrap-up"
        };
        var newIdeaBudget = index switch
        {
            1 => 1,
            2 => 1,
            3 => 1,
            4 => 2,
            5 => 2,
            _ => 2
        };
        return new AiLadderSlotContract(
            index,
            totalCount,
            descriptor,
            band,
            newIdeaBudget,
            true,
            true,
            true,
            new[] { "title", "intro", "steps", "run-outcome" },
            new[] { "dry-olympiad-tone", "jump-in-difficulty", "references-to-other-slots", "multiple-new-ideas" });
    }

    public static string BuildSlotContractBlock(AiLadderSlotContract contract, string? concept)
    {
        var conceptValue = string.IsNullOrWhiteSpace(concept) ? "<same-as-user-request>" : concept;
        return $@"TF_LADDER_SLOT
INDEX={contract.Index}
TOTAL={contract.TotalCount}
STEP_ROLE={contract.StepRole}
COMPLEXITY={contract.ComplexityBand}
NEW_IDEA_BUDGET={contract.NewIdeaBudget}
CONCEPT={conceptValue}
REQUIRED_SECTIONS={string.Join('|', contract.RequiredSections)}
ANTI_PATTERNS={string.Join('|', contract.AntiPatterns)}
END_TF_LADDER_SLOT";
    }

    public static string DescribeSlotRole(int index, int totalCount)
    {
        if (index <= 0)
            index = 1;
        if (totalCount <= 0)
            totalCount = 1;
        return SlotDescriptors[Math.Min(index - 1, SlotDescriptors.Length - 1)];
    }

    public static string BuildTitleHint(string? concept, string? currentTitleHint, int index, int totalCount)
    {
        if (!string.IsNullOrWhiteSpace(currentTitleHint))
            return currentTitleHint!.Trim();

        var conceptPart = string.IsNullOrWhiteSpace(concept)
            ? string.Empty
            : $": {concept}";

        return index switch
        {
            1 => $"Первые шаги{conceptPart}",
            2 => $"Новый шаг{conceptPart}",
            3 => $"Спокойная практика{conceptPart}",
            4 => $"Ещё один шаг{conceptPart}",
            _ => totalCount <= 1 ? $"Пошаговое обучение{conceptPart}" : $"Практика{conceptPart}"
        };
    }

    private static string NormalizeConcept(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var value = raw.Trim();
        value = Regex.Replace(value, @"\s+", " ");
        value = value.Trim('"', '«', '»', '\'', '“', '”', '(', ')', '[', ']');
        value = Regex.Replace(value, @"^(именно|только|просто)\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var noise in GenericNoise)
            value = Regex.Replace(value, $@"\b{Regex.Escape(noise)}\b", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\s+", " ").Trim(' ', '-', '—', ':');
        return value.Length is < 2 or > 80 ? string.Empty : value;
    }
}
