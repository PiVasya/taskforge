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
    private static string SanitizeStudentFacingTitle(string? title)
    {
        var clean = (title ?? string.Empty).Trim();
        clean = Regex.Replace(clean, @"^\s*Подготовка\s+к\s+заданию\s+\d+\s*[\.:\-–—]?\s*\d+[\.:\-–—]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        clean = Regex.Replace(clean, @"^\s*Задание\s+\d+(?:\.\d+)?[\.:\-–—]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Черновик задания" : clean;
    }

    private static string SanitizeStudentFacingDescription(string? description)
    {
        var original = (description ?? string.Empty).Replace("\r\n", "\n").Trim();
        var clean = original;
        clean = Regex.Replace(clean, @"^\s*Место\s+в\s+курсе\..*?(?:\n\s*\n|$)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant).TrimStart();
        clean = Regex.Replace(clean, @"^\s*Это\s+подготовительное\s+задание\s+после.*?(?:\n\s*\n|$)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant).TrimStart();
        clean = RemoveInternalRubricSections(clean);
        clean = RemoveInternalRequirementLines(clean);
        clean = CollapseBlankLines(clean).Trim();
        if (string.IsNullOrWhiteSpace(clean))
            clean = FirstStudentFacingParagraph(original);
        clean = WrapKnownCodeTokens(clean);
        return clean;
    }

    private static string RemoveInternalRubricSections(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var kept = new List<string>();
        var skipping = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (IsInternalRubricHeading(line))
            {
                skipping = true;
                continue;
            }

            if (skipping && IsStudentFacingHeading(line))
                skipping = false;

            if (!skipping)
                kept.Add(rawLine);
        }

        return string.Join("\n", kept);
    }

    private static string RemoveInternalRequirementLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var kept = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (LooksLikeInternalRequirementLine(line))
                continue;
            kept.Add(rawLine);
        }

        return string.Join("\n", kept);
    }

    private static bool IsInternalRubricHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return Regex.IsMatch(line, @"^(требования\s+и\s+критерии|критерии\s+при[её]ма|критерии\s+проверки|acceptance\s+criteria|requirements|must\s*not\s*use|ограничения|запрещ[её]нные\s+при[её]мы)\s*[:.]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsStudentFacingHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return Regex.IsMatch(line, @"^(условие|задача|что\s+нужно\s+сделать|подсказка|формат\s+ввода|формат\s+вывода|ввод|вывод|пример|примеры|пояснение)\s*[:.]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeInternalRequirementLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var clean = Regex.Replace(line, @"^[\-•*\d.)\s]+", string.Empty).Trim();
        return Regex.IsMatch(clean, @"^(программа\s+должна\s+использовать|тесты\s+проверяют|нельзя\s+(применять|использовать)|не\s+используйте|запрещается|запрещено|do\s+not\s+use|must\s+not\s+use|use\s+only)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string FirstStudentFacingParagraph(string text)
    {
        foreach (var part in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            var candidate = part.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && !IsInternalRubricHeading(candidate))
                return candidate;
        }
        return text.Trim();
    }

    private static string CollapseBlankLines(string text)
    {
        var normalized = Regex.Replace(text.Replace("\r\n", "\n"), @"[ \t]+\n", "\n", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        return normalized;
    }

    private static string WrapKnownCodeTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var spans = new List<string>();
        string Protect(string value)
        {
            var marker = $"\u0001{spans.Count}\u0002";
            spans.Add(value);
            return marker;
        }

        var result = Regex.Replace(text, @"`[^`]*`", m => Protect(m.Value), RegexOptions.CultureInvariant);

        string WrapPattern(string input, string pattern, Func<Match, string> replacement)
        {
            return Regex.Replace(input, pattern, m => Protect(replacement(m)), RegexOptions.CultureInvariant);
        }

        // Formatting helper only: it does not generate or replace solutions. Keep it
        // broad enough for multiple programming languages so student-facing text gets
        // readable inline-code styling without forcing a C#-specific template.
        result = WrapPattern(result, @"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b[A-Za-z_][A-Za-z0-9_]*\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b(?:std::)?(?:cin|cout|printf|scanf|println|print|input|read|readln|writeln)\b", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b(?:string|int|long|double|float|bool|char|var|auto|String|Scanner)\b", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b[A-Za-z_][A-Za-z0-9_]*\[[^\n`]*?\]", m => "`" + m.Value + "`");

        for (var i = 0; i < spans.Count; i++)
            result = result.Replace($"\u0001{i}\u0002", spans[i]);

        return result;
    }

    private static List<string> BuildPublicTags(IEnumerable<string> tags)
    {
        var result = new List<string>();
        foreach (var tag in tags)
        {
            var clean = (tag ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;
            var lower = clean.ToLowerInvariant();
            if (lower == "learning-bridge" || lower.StartsWith("learning-bridge-step-", StringComparison.Ordinal) || lower.StartsWith("skill:", StringComparison.Ordinal) || lower.StartsWith("input-onboarding", StringComparison.Ordinal))
                continue;
            if (!result.Contains(clean, StringComparer.OrdinalIgnoreCase)) result.Add(clean);
        }
        return result;
    }

    private static List<string> MergeTags(List<string> tags, IEnumerable<string> required)
    {
        var result = new List<string>();
        foreach (var tag in tags.Concat(required))
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var clean = tag.Trim();
            if (!result.Contains(clean, StringComparer.OrdinalIgnoreCase)) result.Add(clean);
        }
        return result;
    }


}
