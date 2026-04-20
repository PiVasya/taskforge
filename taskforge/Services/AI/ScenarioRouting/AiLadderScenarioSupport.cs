using System.Text.RegularExpressions;
using taskforge.Data.Models.DTO.AI;

namespace taskforge.Services.AI;

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
        "тот же навык, но уже с пользовательским вводом или с чуть более живым вариантом использования",
        "первое аккуратное усложнение: добавляется одна новая маленькая идея поверх базы",
        "спокойная комбинированная практика: два связанных шага, но без резкого скачка сложности",
        "чуть более взрослая задача на тот же навык в реалистичной формулировке",
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
            ? "Сохраняй тему пользователя и не подменяй её другой идеей."
            : $"Точная учебная цель серии: «{concept}». Не подменяй её соседней темой и не уезжай в другую конструкцию.";

        return $@"Стиль лесенки должен быть как у очень понятного первого учебного задания.
- Это не олимпиадная формулировка и не сухой code-test.
- Каждая задача должна ощущаться как маленькое обучение, а не как голая проверка.
- Начинай с короткого дружелюбного вступления.
- Дальше веди ученика через блок «Следуй шагам:».
- Шаги должны быть нумерованными и конкретными.
- После важных шагов давай короткие пояснения в скобках простым языком.
- Финал должен говорить, что именно ученик увидит после запуска.
- Каждая задача должна быть самостоятельной и завершённой.
- В серии из {count} задач рост сложности должен быть очень плавным.
- Не начинай описание с сухого шаблона «Напиши программу...» без живого объяснения.
{conceptLine}";
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
        var baseTitle = !string.IsNullOrWhiteSpace(currentTitleHint)
            ? currentTitleHint!.Trim()
            : !string.IsNullOrWhiteSpace(concept)
                ? $"Первые шаги: {concept}"
                : "Пошаговое обучение";
        return totalCount <= 1 ? baseTitle : $"{baseTitle} · шаг {index}";
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
