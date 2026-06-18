using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed partial class DraftAuthorExecutor
{
    internal static string EnsureLearningBridgeTutorialStyle(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var clean = (draft.Description ?? string.Empty).Trim();
        if (!LooksLikeLearningBridgeDraft(draft, bridge))
            return WrapKnownCodeTokens(CollapseBlankLines(clean));

        clean = StripDryTaskSections(clean);
        if (LooksLikeTutorialStyle(clean) && !NeedsTutorialRewrite(clean))
            return WrapKnownCodeTokens(CollapseBlankLines(clean));

        return EnsureGenericTutorialShape(clean, draft, bridge, stepIndex);
    }

    private static bool LooksLikeLearningBridgeDraft(DraftSpec draft, CourseSkillBridgeContext bridge)
    {
        if (bridge.BridgePlan is { Count: > 0 }) return true;
        if (draft.Extra["courseSkillMapStep"] is not null) return true;
        if (draft.Extra["bridgeSkillId"] is not null) return true;
        if (draft.Extra["skillBridge"] is not null) return true;
        return false;
    }

    private static bool LooksLikeTutorialStyle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        var hasSteps = lower.Contains("следуй шагам") || lower.Contains("шаг 1") || Regex.IsMatch(lower, @"(^|\n)\s*1\.\s+", RegexOptions.CultureInvariant);
        var hasTeachingTone = lower.Contains("давай") || lower.Contains("научимся") || lower.Contains("научись") || lower.Contains("запусти") || lower.Contains("проверь") || lower.Contains("попробуй");
        return hasSteps && hasTeachingTone;
    }

    private static bool NeedsTutorialRewrite(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("самый короткий") || lower.Contains("короткий короткий") || lower.Contains("code golf") || lower.Contains("гольф"))
            return true;
        if (!lower.Contains("запусти") && !lower.Contains("проверь"))
            return true;
        if (HasDryTaskHeading(text)) return true;
        if (Regex.IsMatch(text, @"для\s+ввода\s+`[^`]*\n[^`]*`", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return false;
    }

    private static string EnsureGenericTutorialShape(string description, DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var stepHint = DescribeBridgeStep(bridge, stepIndex, description);
        var inputNote = BuildFriendlyInputNote(draft);
        var steps = BuildTutorialStepsFromReferenceSolution(draft.ReferenceSolution, draft.Language).ToList();
        if (steps.Count == 0)
        {
            steps.Add($"1. Разбери новый приём: {WrapInline(stepHint)}.");
            steps.Add("(Это маленький шаг, который понадобится в следующей задаче.)");
            steps.Add("2. Напиши понятное минимально необходимое решение.");
            steps.Add("(Не добавляй лишние проверки и темы, которые здесь ещё не нужны.)");
        }

        var check = BuildFriendlyCheckSentence(draft);
        var intro = BuildFriendlyIntro(bridge, stepIndex, stepHint);
        var noteBlock = string.IsNullOrWhiteSpace(inputNote) ? string.Empty : inputNote + "\n\n";
        var text = $$"""
{{intro}}

{{noteBlock}}Следуй шагам:
{{string.Join("\n", steps)}}

{{check}}
""";
        return WrapKnownCodeTokens(CollapseBlankLines(text.Trim()));
    }

    private static string BuildFriendlyIntro(CourseSkillBridgeContext bridge, int stepIndex, string stepHint)
    {
        var normalized = NormalizeForTutorialText(stepHint);
        if (normalized.Contains("readline") || normalized.Contains("ввод") || normalized.Contains("считать") || normalized.Contains("прочит"))
            return "Давай научимся получать данные из консоли маленькими шагами.";
        if (normalized.Contains("parse") || normalized.Contains("числ") || normalized.Contains("преобраз"))
            return "Давай научимся превращать введённый текст в число.";
        return "Давай сделаем маленький учебный шаг перед следующей задачей.";
    }

    private static string BuildFriendlyInputNote(DraftSpec draft)
    {
        var input = draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Input))?.Input;
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var lines = SplitInputLines(input).ToList();
        if (lines.Count > 1)
            return lines.Count == 2
                ? "Будем считать, что пользователь вводит два корректных значения: первое с новой строки и второе с новой строки."
                : $"Будем считать, что пользователь вводит {lines.Count} корректных значения, каждое с новой строки.";
        return "Будем считать, что пользователь вводит корректное значение.";
    }

    private static string BuildFriendlyCheckSentence(DraftSpec draft)
    {
        var sampleInput = draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Input))?.Input;
        var expected = draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ExpectedOutput))?.ExpectedOutput;
        if (string.IsNullOrWhiteSpace(sampleInput) || string.IsNullOrWhiteSpace(expected))
            return "Запусти код и проверь, что программа делает именно этот маленький шаг.";

        var inputText = DescribeInputForStudent(sampleInput);
        var outputText = ToInlineCode(CleanOneLineValue(expected));
        return $"Запусти код и проверь: если ввести {inputText}, на экране появится {outputText}.";
    }

    private static IEnumerable<string> BuildTutorialStepsFromReferenceSolution(string? solution, string? language)
    {
        var groups = BuildCodeGroups(solution).ToList();
        if (groups.Count == 0) yield break;

        if (groups.Count > 5)
            groups = CompactCodeGroups(groups);
        if (groups.Count > 5)
            groups = groups.Take(5).ToList();

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            yield return $"{i + 1}. {BuildStepAction(group)}";
            yield return $"({BuildStepExplanation(group)})";
        }
    }

    private sealed record CodeGroup(string Kind, List<string> Lines);

    private static IEnumerable<CodeGroup> BuildCodeGroups(string? solution)
    {
        if (string.IsNullOrWhiteSpace(solution)) yield break;
        var groups = new List<CodeGroup>();
        foreach (var raw in solution.Replace("\r\n", "\n").Split('\n'))
        {
            var line = NormalizeCodeLine(raw);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (IsCommentOnlyCodeLine(line)) continue;
            if (line is "{" or "}" or "};") continue;

            var kind = ClassifyCodeLine(line);
            var last = groups.LastOrDefault();
            if (last is not null && last.Kind == kind && CanGroupCodeLines(kind))
            {
                last.Lines.Add(line);
                continue;
            }

            groups.Add(new CodeGroup(kind, new List<string> { line }));
        }

        foreach (var group in groups)
            yield return group;
    }

    private static List<CodeGroup> CompactCodeGroups(List<CodeGroup> groups)
    {
        var result = new List<CodeGroup>();
        foreach (var group in groups)
        {
            var last = result.LastOrDefault();
            if (last is not null && (last.Kind == group.Kind || group.Kind == "start" && last.Kind == "start"))
            {
                last.Lines.AddRange(group.Lines);
                continue;
            }
            result.Add(new CodeGroup(group.Kind, group.Lines.ToList()));
        }

        if (result.Count <= 5) return result;

        var import = result.FirstOrDefault(x => x.Kind == "import");
        var start = result.FirstOrDefault(x => x.Kind == "start");
        if (import is not null && start is not null)
        {
            start.Lines.InsertRange(0, import.Lines);
            result.Remove(import);
        }

        return result;
    }

    private static string BuildStepAction(CodeGroup group)
    {
        var code = JoinCodeChips(group.Lines);
        return group.Kind switch
        {
            "import" => $"Подключи нужную библиотеку: {code}",
            "start" => $"Напиши начало программы: {code}",
            "input" => $"Считай данные из консоли: {code}",
            "parse" => $"Преобразуй введённый текст в число: {code}",
            "compute" => $"Выполни вычисление: {code}",
            "output" => $"Выведи результат: {code}",
            _ => $"Напиши строку: {code}"
        };
    }

    private static string BuildStepExplanation(CodeGroup group)
    {
        return group.Kind switch
        {
            "import" => "Эта строка подключает команды, которые нужны программе.",
            "start" => "Так начинается основная часть программы.",
            "input" => group.Lines.Count > 1 ? "Эти строки получают значения, которые пользователь вводит с клавиатуры." : "Эта строка получает значение, которое пользователь вводит с клавиатуры.",
            "parse" => group.Lines.Count > 1 ? "Эти строки превращают введённый текст в числа." : "Эта строка превращает введённый текст в число.",
            "compute" => "Здесь выполняется простое вычисление.",
            "output" => "Эта строка показывает результат на экране.",
            _ => "Эта строка нужна для текущего маленького шага."
        };
    }

    private static string NormalizeCodeLine(string raw)
    {
        var line = raw.Trim();
        line = Regex.Replace(line, @"\s+", " ", RegexOptions.CultureInvariant);
        return line;
    }

    private static bool IsCommentOnlyCodeLine(string line)
        => line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal);

    private static string ClassifyCodeLine(string line)
    {
        var normalized = NormalizeForTutorialText(line);
        if (normalized.Contains("#include") || normalized.StartsWith("using ") || normalized.StartsWith("import ") || normalized.StartsWith("from "))
            return "import";
        if (normalized.Contains("main") || normalized.Contains("class program") || normalized.Contains("namespace") || normalized.Contains("public class"))
            return "start";
        if (normalized.Contains("readline") || normalized.Contains("readln") || normalized.Contains("scanf") || normalized.Contains("cin") || normalized.Contains("input("))
            return "input";
        if (normalized.Contains("parse") || normalized.Contains("toint") || normalized.Contains("convert.") || normalized.Contains("int(input") || normalized.Contains("stoi") || normalized.Contains("strconv"))
            return "parse";
        if (normalized.Contains("writeline") || normalized.Contains("write(") || normalized.Contains("cout") || normalized.Contains("printf") || normalized.Contains("println") || normalized.StartsWith("print"))
            return "output";
        if (Regex.IsMatch(line, @"=.+[+\-*/%]", RegexOptions.CultureInvariant))
            return "compute";
        return "other";
    }

    private static bool CanGroupCodeLines(string kind)
        => kind is "import" or "start" or "input" or "parse" or "output" or "compute";

    private static string JoinCodeChips(IEnumerable<string> lines)
    {
        var chips = lines.Select(ToInlineCode).ToList();
        if (chips.Count == 0) return "`...`";
        if (chips.Count == 1) return chips[0];
        if (chips.Count == 2) return chips[0] + " и " + chips[1];
        return string.Join(", ", chips.Take(chips.Count - 1)) + " и " + chips[^1];
    }

    private static string DescribeInputForStudent(string input)
    {
        var lines = SplitInputLines(input).ToList();
        if (lines.Count > 1 && lines.Count <= 4)
            return string.Join(" и ", lines.Select(ToInlineCode)) + " с новой строки";
        return ToInlineCode(CleanOneLineValue(input));
    }

    private static IEnumerable<string> SplitInputLines(string input)
    {
        foreach (var line in input.Replace("\r\n", "\n").Trim('\r', '\n').Split('\n'))
        {
            var clean = line.Trim();
            if (!string.IsNullOrEmpty(clean)) yield return clean;
        }
    }

    private static string CleanOneLineValue(string value)
        => value.Replace("\r\n", "\n").Replace("\n", "\\n").Trim();

    private static string ToInlineCode(string value)
        => "`" + EscapeBackticks(value) + "`";

    private static string WrapInline(string value)
        => value.Contains('`') ? value : ToInlineCode(value);

    private static string EscapeBackticks(string value)
        => (value ?? string.Empty).Replace("`", "' ");

    private static string NormalizeForTutorialText(string? value)
        => Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static bool HasDryTaskHeading(string text)
        => Regex.IsMatch(text ?? string.Empty, @"(^|\n)\s*(формат\s+ввода|формат\s+вывода|пример|ввод|вывод|критерии|тесты\s+проверяют)\s*:?\s*(\n|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string StripDryTaskSections(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var skipping = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"^(формат\s+ввода|формат\s+вывода|пример|ввод|вывод|критерии|тесты\s+проверяют)\s*:?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                skipping = true;
                continue;
            }

            if (skipping && (Regex.IsMatch(line, @"^\d+\.\s+", RegexOptions.CultureInvariant) || line.Contains("следуй шагам", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(line)))
            {
                if (!string.IsNullOrWhiteSpace(line)) skipping = false;
            }

            if (!skipping)
                kept.Add(raw);
        }

        var clean = string.Join("\n", kept).Trim();
        return string.IsNullOrWhiteSpace(clean) ? text : clean;
    }

    private static string DescribeBridgeStep(CourseSkillBridgeContext bridge, int stepIndex, string fallbackDescription)
    {
        var step = bridge.BridgePlan != null && stepIndex >= 0 && stepIndex < bridge.BridgePlan.Count
            ? bridge.BridgePlan[stepIndex]
            : null;
        var candidates = new List<string>();
        if (step is not null)
        {
            candidates.Add(step["titleHint"]?.ToString() ?? string.Empty);
            candidates.AddRange(ReadStringArray(step["introducedSkills"]));
            candidates.Add(step["reason"]?.ToString() ?? string.Empty);
        }
        candidates.Add(FirstSentence(fallbackDescription));

        return candidates
            .Select(x => Regex.Replace(x ?? string.Empty, @"\s+", " ").Trim().TrimEnd('.', ':', ';'))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "нужно выполнить маленькое действие из текущей темы";
    }

    private static string FirstSentence(string text)
    {
        var clean = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        if (string.IsNullOrWhiteSpace(clean)) return "нужно выполнить маленькое действие из текущей темы";
        var match = Regex.Match(clean, @"^(.{20,220}?[.!?])\s", RegexOptions.CultureInvariant);
        if (match.Success) return match.Groups[1].Value.Trim();
        return clean.Length <= 220 ? clean : clean[..220].TrimEnd() + "...";
    }
}
