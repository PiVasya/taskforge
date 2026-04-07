using System.Text;
using System.Text.RegularExpressions;
using taskforge.Data.Models.DTO.AI;

namespace taskforge.Services.AI;

public static class FoundryChatIntentService
{
    private static readonly string[] SkillPhrases =
    [
        "ввод и вывод", "ввод данных", "вывод данных", "форматированный вывод", "арифметические операции",
        "условные операторы", "ветвления", "циклы", "вложенные циклы", "массивы", "двумерные массивы",
        "строки", "char array", "функции", "рекурсия", "сортировка", "поиск", "графы", "динамическое программирование",
        "stdin", "stdout", "cin", "cout", "while", "for", "if", "switch"
    ];

    private static readonly string[] TrashWords =
    [
        "базовые", "данного", "курса", "мега", "интересными", "сгенерируй", "задачи", "задания", "пакет",
        "сделай", "хочу", "нужно", "надо", "без", "для", "этого"
    ];

    public static AiFoundryChatResolveResponseDto Resolve(AiFoundryChatResolveRequestDto request)
    {
        var messages = request.Messages ?? new List<AiFoundryChatMessageDto>();
        var userTexts = messages
            .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(x => Normalize(x.Content))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var combined = string.Join("\n", userTexts);
        var latest = userTexts.LastOrDefault() ?? string.Empty;

        var assignmentType = ResolveAssignmentType(combined, request.AssignmentType);
        var difficulty = ResolveDifficulty(combined, request.Difficulty);
        var count = ResolveCount(combined, request.Count);
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "topic-pack" : request.Mode!.Trim();
        var goals = ExtractGoals(combined);
        var constraints = ExtractConstraints(combined);
        var warnings = BuildWarnings(goals, combined);
        var prompt = BuildPrompt(goals, constraints, combined, assignmentType, difficulty, count);
        var notes = BuildNotes(messages, constraints);
        var title = BuildTitle(goals, assignmentType);
        var assistant = BuildAssistantMessage(goals, constraints, warnings, difficulty, count, assignmentType, latest);

        return new AiFoundryChatResolveResponseDto
        {
            SessionTitle = title,
            AssistantMessage = assistant,
            Plan = new AiFoundryChatPlanDto
            {
                Prompt = prompt,
                AssignmentType = assignmentType,
                Difficulty = difficulty,
                Count = count,
                Mode = mode,
                Notes = notes,
                Goals = goals,
                Constraints = constraints,
                Warnings = warnings,
            }
        };
    }

    private static string Normalize(string? value)
        => Regex.Replace((value ?? string.Empty).Trim(), "\\s+", " ");

    private static string ResolveAssignmentType(string text, string? fallback)
    {
        var low = text.ToLowerInvariant();
        if (low.Contains("тест") || low.Contains("quiz") || low.Contains("вариант ответа")) return "test";
        if (low.Contains("матем") || low.Contains("формула") || low.Contains("уравн")) return "math";
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
        return "code-test";
    }

    private static int ResolveDifficulty(string text, int? fallback)
    {
        var m = Regex.Match(text, @"(?:сложност[ьи]|difficulty)\s*[:=]?\s*([1-5])", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var explicitValue)) return explicitValue;
        var low = text.ToLowerInvariant();
        if (low.Contains("олимпиад") || low.Contains("очень сложно") || low.Contains("hard")) return 5;
        if (low.Contains("сложн") || low.Contains("advanced")) return 4;
        if (low.Contains("базов") || low.Contains("началь") || low.Contains("прост") || low.Contains("easy")) return 2;
        return Math.Clamp(fallback ?? 3, 1, 5);
    }

    private static int ResolveCount(string text, int? fallback)
    {
        var m = Regex.Match(text, @"(\d{1,2})\s*(?:задач|задани|items|item)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var explicitValue)) return Math.Clamp(explicitValue, 1, 12);
        return Math.Clamp(fallback ?? 5, 1, 12);
    }

    private static List<string> ExtractGoals(string text)
    {
        var low = text.ToLowerInvariant();
        var results = new List<string>();
        foreach (var phrase in SkillPhrases)
        {
            if (low.Contains(phrase.ToLowerInvariant())) results.Add(phrase);
        }

        foreach (Match match in Regex.Matches(low, @"(?:на тему|по теме|про|по)\s+([а-яa-z0-9+\- ]{4,40})", RegexOptions.IgnoreCase))
        {
            var candidate = Normalize(match.Groups[1].Value).Trim(' ', '.', ',', ';', ':', '-', '—');
            if (IsMeaningfulPhrase(candidate)) results.Add(candidate);
        }

        if (!results.Any())
        {
            if (low.Contains("ввод") || low.Contains("вывод")) results.Add("ввод и вывод данных");
            if (low.Contains("строк")) results.Add("строки");
            if (low.Contains("массив")) results.Add("массивы");
        }

        return results
            .Select(Normalize)
            .Where(IsMeaningfulPhrase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static bool IsMeaningfulPhrase(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 1)
        {
            var low = normalized.ToLowerInvariant();
            if (TrashWords.Contains(low)) return false;
            if (low.Length < 5) return false;
        }
        return true;
    }

    private static List<string> ExtractConstraints(string text)
    {
        var constraints = new List<string>();
        foreach (Match match in Regex.Matches(text, @"без\s+([а-яa-z0-9+\- ]{2,40})", RegexOptions.IgnoreCase))
        {
            var c = Normalize(match.Groups[1].Value).Trim(' ', '.', ',', ';', ':');
            if (c.Length >= 2) constraints.Add($"без {c}");
        }

        if (text.Contains("одна учебная цель", StringComparison.OrdinalIgnoreCase) || text.Contains("не смешивать", StringComparison.OrdinalIgnoreCase))
            constraints.Add("каждая задача должна иметь одну учебную цель");
        if (text.Contains("интересн", StringComparison.OrdinalIgnoreCase)) constraints.Add("избегать скучных и однотипных сюжетов");
        if (text.Contains("mega", StringComparison.OrdinalIgnoreCase) || text.Contains("мега", StringComparison.OrdinalIgnoreCase))
            constraints.Add("использовать выразительные, но не абсурдные сюжеты");

        return constraints.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static List<string> BuildWarnings(List<string> goals, string text)
    {
        var warnings = new List<string>();
        if (!goals.Any()) warnings.Add("Не удалось надёжно выделить учебные цели — стоит уточнить навыки в чате.");
        if (goals.Any(g => g.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 1))
            warnings.Add("Некоторые цели выглядят слишком короткими — planner дополнительно нормализует их.");
        if (text.Length < 24) warnings.Add("Запрос очень короткий — лучше описать формат задач, стиль и ограничения подробнее.");
        return warnings;
    }

    private static string BuildPrompt(List<string> goals, List<string> constraints, string text, string assignmentType, int difficulty, int count)
    {
        var goalsPart = goals.Any()
            ? string.Join(", ", goals)
            : "ввод и вывод данных, одна учебная цель на задачу";
        var constraintsPart = constraints.Any()
            ? " Ограничения: " + string.Join("; ", constraints) + "."
            : string.Empty;
        return $"Собери пакет из {count} {assignmentType} задач сложности {difficulty}/5. Основные учебные цели: {goalsPart}. Не дроби запрос на отдельные слова, работай только с осмысленными навыками и микроцелями.{constraintsPart} Исходное пожелание пользователя: {Normalize(text)}";
    }

    private static string BuildNotes(List<AiFoundryChatMessageDto> messages, List<string> constraints)
    {
        var recent = messages
            .TakeLast(6)
            .Select(x => $"{x.Role}: {Normalize(x.Content)}")
            .ToList();
        var sb = new StringBuilder();
        if (constraints.Any()) sb.Append("Выявленные ограничения: ").Append(string.Join("; ", constraints)).AppendLine();
        if (recent.Any())
        {
            sb.AppendLine("Контекст диалога:");
            foreach (var line in recent) sb.Append("- ").AppendLine(line);
        }
        return sb.ToString().Trim();
    }

    private static string BuildTitle(List<string> goals, string assignmentType)
    {
        var baseTitle = goals.FirstOrDefault() ?? "Новый пакет";
        return $"{baseTitle} · {assignmentType}";
    }

    private static string BuildAssistantMessage(List<string> goals, List<string> constraints, List<string> warnings, int difficulty, int count, string assignmentType, string latest)
    {
        var sb = new StringBuilder();
        sb.Append("Я понял запрос и собрал черновой Foundry-план.");
        sb.Append(" Тип: ").Append(assignmentType).Append(", сложность: ").Append(difficulty).Append("/5, количество: ").Append(count).Append('.');
        if (goals.Any()) sb.Append(" Цели: ").Append(string.Join(", ", goals)).Append('.');
        if (constraints.Any()) sb.Append(" Ограничения: ").Append(string.Join("; ", constraints)).Append('.');
        if (!string.IsNullOrWhiteSpace(latest)) sb.Append(" Последнее уточнение учтено: «").Append(latest).Append("».");
        if (warnings.Any()) sb.Append(" Внимание: ").Append(string.Join(" ", warnings));
        sb.Append(" Можно сразу применить этот план к форме или запустить генерацию пакета.");
        return sb.ToString();
    }
}
