using System.ComponentModel;
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
        var issues = new JsonArray();
        var description = draft["description"]?.ToString() ?? string.Empty;
        if (description.Length < 120) issues.Add("description is probably too short for a student-facing assignment");
        if ((draft["assignmentType"]?.ToString() ?? "") == "code-test")
        {
            if (!ContainsAny(description, "ввод", "вход", "input", "stdin", "формат ввода"))
                issues.Add("code-test description should explain input format");
            if (!ContainsAny(description, "вывод", "выход", "output", "stdout", "формат вывода"))
                issues.Add("code-test description should explain output format");
        }

        return Task.FromResult(new JsonObject
        {
            ["isAccepted"] = issues.Count == 0,
            ["score"] = issues.Count == 0 ? 92 : Math.Max(45, 90 - issues.Count * 15),
            ["issues"] = issues
        });
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
