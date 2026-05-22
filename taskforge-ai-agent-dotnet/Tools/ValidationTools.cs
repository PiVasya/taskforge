using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Tools;

public sealed class ValidationTools
{
    private readonly AgentRunContextAccessor _contextAccessor;

    public ValidationTools(AgentRunContextAccessor contextAccessor) => _contextAccessor = contextAccessor;

    [Description("Validate TaskForge assignment draft shape before it becomes an artifact or hidden draft.")]
    public Task<JsonObject> ValidateDraftShapeAsync([Description("Draft JSON object.")] JsonObject draft)
    {
        var issues = new JsonArray();
        var assignmentType = draft["assignmentType"]?.ToString() ?? draft["type"]?.ToString() ?? "code-test";
        var title = draft["title"]?.ToString();
        var description = draft["description"]?.ToString() ?? draft["condition"]?.ToString() ?? draft["body"]?.ToString();

        if (string.IsNullOrWhiteSpace(title)) issues.Add("title is required");
        if (string.IsNullOrWhiteSpace(description)) issues.Add("description is required");
        if (!int.TryParse(draft["difficulty"]?.ToString(), out var difficulty) || difficulty is < 1 or > 3) issues.Add("difficulty must be 1..3");

        if (assignmentType == "code-test")
        {
            var solution = draft["referenceSolution"]?.ToString()
                ?? draft["solution"]?.ToString()
                ?? draft["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(solution)) issues.Add("referenceSolution is required for code-test");

            var publicTests = draft["publicTests"] as JsonArray;
            if (publicTests == null || publicTests.Count < 2)
            {
                issues.Add("at least 2 publicTests are required for code-test");
            }
            else
            {
                AddTestIssues(publicTests, false, issues);
            }

            var hiddenTests = draft["hiddenTests"] as JsonArray;
            if (hiddenTests == null || hiddenTests.Count < 2)
            {
                issues.Add("at least 2 hiddenTests are required for code-test");
            }
            else
            {
                AddTestIssues(hiddenTests, true, issues);
            }

            if (publicTests != null && hiddenTests != null)
            {
                var publicFingerprints = publicTests.OfType<JsonObject>().Select(TestFingerprint).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
                var hiddenFingerprints = hiddenTests.OfType<JsonObject>().Select(TestFingerprint).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
                if (hiddenFingerprints.Count == 0 || hiddenFingerprints.All(publicFingerprints.Contains))
                    issues.Add("hiddenTests must include cases that are not identical to publicTests");
            }
        }

        return Task.FromResult(new JsonObject
        {
            ["ok"] = issues.Count == 0,
            ["assignmentType"] = assignmentType,
            ["issues"] = issues
        });
    }


    private static void AddTestIssues(JsonArray tests, bool hidden, JsonArray issues)
    {
        for (var i = 0; i < tests.Count; i++)
        {
            if (tests[i] is not JsonObject test)
            {
                issues.Add($"{(hidden ? "hidden" : "public")} test #{i + 1} must be an object");
                continue;
            }

            if (!test.ContainsKey("expectedOutput") && !test.ContainsKey("output"))
                issues.Add($"{(hidden ? "hidden" : "public")} test #{i + 1} expectedOutput is required");
        }
    }

    private static string TestFingerprint(JsonObject test)
    {
        if (!test.ContainsKey("expectedOutput") && !test.ContainsKey("output"))
            return string.Empty;

        var input = (test["input"]?.ToString() ?? string.Empty).Replace("\r\n", "\n");
        var expected = (test["expectedOutput"]?.ToString() ?? test["output"]?.ToString() ?? string.Empty).Replace("\r\n", "\n");
        return $"{input}=>{expected}";
    }

    [Description("Run code tests through TaskForge backend compiler service. Use it for code-test draft validation, not for theoretical reasoning.")]
    public async Task<JsonObject> RunCodeTestsAsync(
        [Description("Programming language: cpp, csharp, java, python, javascript, pascal.")] string language,
        [Description("Code to execute.")] string code,
        [Description("Test cases with input and expected output.")] List<TestCaseSpec> tests)
    {
        var context = _contextAccessor.Current;
        if (string.IsNullOrWhiteSpace(code) || tests.Count == 0)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["reason"] = "Code and at least one test case are required."
            };
        }

        return await context.Api.RunTestsAsync(new TestRunRequest
        {
            RunId = context.Job.RunId,
            WorkerId = context.WorkerId,
            Language = language,
            Code = code,
            Tests = tests
        }, context.CancellationToken);
    }

    [Description("Critique a draft deterministically for common educational quality problems before asking the model critic.")]
    public Task<JsonObject> StaticDraftCritiqueAsync(JsonObject draft)
    {
        var blocking = new JsonArray();
        var advisory = new JsonArray();
        var title = draft["title"]?.ToString() ?? string.Empty;
        var description = draft["description"]?.ToString() ?? string.Empty;
        if (description.Length < 120) advisory.Add("description is short; consider adding clearer input/output notes, but this is not a blocker if tests and runner pass");

        if (ContainsAny(title, "Подготовка к заданию", "AI-черновик", "hidden draft"))
            blocking.Add("title contains service/debug wording instead of a student-facing assignment name");
        if (Regex.IsMatch(title, @"задани[ея]\s+\d+\s+\d+\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            blocking.Add("title looks like a technical insertion marker, not a course assignment title");

        if (ContainsAny(description, "Место в курсе", "подготовительное задание после", "перед Задание", "после List<T>", "sourceAgent", "AI-черновик"))
            blocking.Add("description contains service/course-placement wording that should stay in metadata, not in the student-facing text");

        if (LooksLikeInternalRubric(description))
            blocking.Add("description exposes internal validator/rubric wording; keep acceptanceCriteria, mustNotUse and hard restrictions in metadata, not in the student-facing text");

        if (HasUnwrappedCodeToken(description))
            blocking.Add("student-facing code/API tokens must be wrapped in backticks");

        var tags = draft["tags"]?.ToString() ?? string.Empty;
        var extra = draft["extra"] as JsonObject;
        var isLearningBridge = tags.Contains("learning-bridge", StringComparison.OrdinalIgnoreCase)
                               || extra?["skillBridge"] is not null
                               || extra?["missingBridgeSkills"] is not null;
        if (isLearningBridge)
        {
            var introducedSkills = ReadStringArray(extra?["introducedSkills"]).ToList();
            if (introducedSkills.Count == 0)
                blocking.Add("learning-bridge draft must declare introducedSkills in extra metadata");
            if (introducedSkills.Count > 1)
                advisory.Add("learning-bridge draft declares several introducedSkills strings; bridge critic will normalize them to skill ids before deciding whether this is blocking");
        }

        if ((draft["assignmentType"]?.ToString() ?? "") == "code-test")
        {
            if (!ContainsAny(description, "ввод", "вход", "input", "stdin", "формат ввода"))
                advisory.Add("code-test description should explain input format");
            if (!ContainsAny(description, "вывод", "выход", "output", "stdout", "формат вывода"))
                advisory.Add("code-test description should explain output format");
        }

        var issues = new JsonArray();
        foreach (var item in blocking) issues.Add(item?.DeepClone());
        foreach (var item in advisory) issues.Add(item?.DeepClone());

        return Task.FromResult(new JsonObject
        {
            ["isAccepted"] = blocking.Count == 0,
            ["score"] = blocking.Count == 0 ? (advisory.Count == 0 ? 92 : 82) : Math.Max(35, 92 - blocking.Count * 22 - advisory.Count * 4),
            ["issues"] = issues,
            ["blockingIssues"] = blocking,
            ["advisoryIssues"] = advisory
        });
    }


    private static IEnumerable<string> ReadStringArray(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var value = item?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }
        else if (!string.IsNullOrWhiteSpace(node?.ToString()))
        {
            foreach (var item in node!.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return item;
        }
    }

    private static bool LooksLikeInternalRubric(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var withoutCodeSpans = Regex.Replace(description, @"`[^`]*`", string.Empty);
        if (Regex.IsMatch(withoutCodeSpans, @"\b(acceptance\s+criteria|must\s*not\s*use|quality\s+gate)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(withoutCodeSpans, @"(^|\n)\s*(требования\s+и\s+критерии|критерии\s+при[её]ма|критерии\s+проверки|ограничения)\s*[:.]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(withoutCodeSpans, @"(^|\n)\s*[-•*\d.)\s]*(программа\s+должна\s+использовать|тесты\s+проверяют|нельзя\s+(применять|использовать)|не\s+используйте|запрещается|запрещено)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return false;
    }

    private static bool HasUnwrappedCodeToken(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var withoutCodeSpans = Regex.Replace(description, @"`[^`]*`", string.Empty);
        var patterns = new[]
        {
            @"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\s*\(",
            @"\b(?:std::)?(?:cin|cout|printf|scanf|println|print|input|read|readln|writeln)\b",
            @"\b(?:String|Scanner|string|int|long|double|float|bool|char|var|auto)\b",
            @"\b[A-Za-z_][A-Za-z0-9_]*\s*\([^\n`]*?\)",
            @"\b[A-Za-z_][A-Za-z0-9_]*\[[^\n`]*?\]"
        };

        return patterns.Any(pattern => Regex.IsMatch(withoutCodeSpans, pattern, RegexOptions.CultureInvariant));
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
