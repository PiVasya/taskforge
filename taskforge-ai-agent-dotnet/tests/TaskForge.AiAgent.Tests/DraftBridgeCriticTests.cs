using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Workflows;
using TaskForge.AiAgent.Workflows.Executors;
using Xunit;

namespace TaskForge.AiAgent.Tests;

public sealed class DraftBridgeCriticTests
{
    [Fact]
    public void ReadSingleValueBridge_AllowsReadLineAndSimpleIntParse()
    {
        var state = BuildBridgeState(new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "Считать одно число и вывести его",
            Description = "Считай одну строку из консоли, преобразуй ее в целое число и выведи само число без лишнего текста.",
            Language = "csharp",
            ReferenceSolution = """
using System;

class Program
{
    static void Main()
    {
        string? s = Console.ReadLine();
        int n = int.Parse(s!);
        Console.WriteLine(n);
    }
}
""",
            Extra = BuildDraftExtra(modelIntroducedSkillIds: ["input-считать-одну-строку-из-консоли-и-преобразовать-е-в-чис"])
        });

        var critique = DraftCriticExecutor.EvaluateBridgeConsistency(state);

        Assert.True(string.Equals("true", critique["isAccepted"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(critique["blockingIssues"]!.AsArray());
    }

    [Fact]
    public void ReadSingleValueBridge_RejectsSplitWhenSplitIsForbidden()
    {
        var state = BuildBridgeState(new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "Считать одно число",
            Description = "Считай одно значение.",
            Language = "csharp",
            ReferenceSolution = """
using System;

class Program
{
    static void Main()
    {
        var parts = Console.ReadLine()!.Split(' ');
        Console.WriteLine(int.Parse(parts[0]));
    }
}
""",
            Extra = BuildDraftExtra()
        });

        var critique = DraftCriticExecutor.EvaluateBridgeConsistency(state);

        Assert.True(string.Equals("false", critique["isAccepted"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(critique["blockingIssues"]!.AsArray().Select(x => x?.ToString() ?? string.Empty), x => x.Contains("Split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadSingleValueBridge_RejectsRegexWhenAdvancedParsingIsForbidden()
    {
        var state = BuildBridgeState(new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "Считать одно число",
            Description = "Считай одно значение.",
            Language = "csharp",
            ReferenceSolution = """
using System;
using System.Text.RegularExpressions;

class Program
{
    static void Main()
    {
        var match = Regex.Match(Console.ReadLine()!, "\\d+");
        Console.WriteLine(int.Parse(match.Value));
    }
}
""",
            Extra = BuildDraftExtra()
        });

        var critique = DraftCriticExecutor.EvaluateBridgeConsistency(state);

        Assert.True(string.Equals("false", critique["isAccepted"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(critique["blockingIssues"]!.AsArray().Select(x => x?.ToString() ?? string.Empty), x => x.Contains("парсинг", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalSkillDetection_DoesNotTreatAdvancedParsingLabelAsSimpleParseInt()
    {
        var ids = CourseSkillAnalyzer.DetectCanonicalSkillIds("сложный парсинг/регулярные выражения");

        Assert.DoesNotContain("parse-int", ids);
        Assert.Contains("regex", ids);
    }

    private static WorkflowState BuildBridgeState(DraftSpec draft)
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();
        var bridge = new CourseSkillBridgeContext(
            BeforeAssignmentId: Guid.Parse("081c525c-b181-4c4c-8fa7-c7a736d889ee"),
            PreviousTitle: "Задание 4",
            AnchorTitle: "Задание 5",
            RequestedSkills: ["input (single value from console)"],
            AcquiredSkills: ["output", "базовые типы int и string"],
            TargetSkills: ["input (считать одно значение из консоли и спарсить в нужный тип)"],
            MissingBridgeSkills: ["input (single value)"],
            Neighborhood: [],
            IsBridgeRequest: true,
            BridgePlan: [BuildPlanStep()],
            Source: "test");

        return new WorkflowState
        {
            Job = new ClaimedAgentJob(Guid.Empty, Guid.Empty, "test", payload, "Сгенерируй обучалку перед задачей на ввод", Guid.Empty, "Основы C#"),
            Draft = draft,
            CourseSkillBridge = bridge
        };
    }

    private static JsonObject BuildPlanStep()
    {
        return new JsonObject
        {
            ["step"] = 0,
            ["skillId"] = "read-single-value-from-console",
            ["titleHint"] = "Считать одно значение из консоли",
            ["mustNotUse"] = new JsonArray("массивы", "Split()", "сложный парсинг/регулярные выражения", "ввод нескольких значений в одной строке"),
            ["assumedSkills"] = new JsonArray("output", "знание базовых типов (int, string)", "умение выводить результат"),
            ["introducedSkills"] = new JsonArray("input (считать одну строку и преобразовать её в число/строку)"),
            ["introducedSkillIds"] = new JsonArray("read-single-value-from-console"),
            ["reason"] = "Этот шаг дает ровно один новый навык — считать одно значение и парсить его."
        };
    }

    private static JsonObject BuildDraftExtra(string[]? modelIntroducedSkillIds = null)
    {
        return new JsonObject
        {
            ["bridgeStepIndex"] = 0,
            ["bridgeSkillId"] = "read-single-value-from-console",
            ["introducedSkillIds"] = new JsonArray("read-single-value-from-console"),
            ["introducedSkills"] = new JsonArray("input (считать одну строку и преобразовать её в число/строку)"),
            ["modelIntroducedSkills"] = new JsonArray("input (считать одну строку из консоли и преобразовать ее в число)"),
            ["modelIntroducedSkillIds"] = ToJsonArray(modelIntroducedSkillIds ?? Array.Empty<string>())
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values)
            arr.Add(value);
        return arr;
    }
}
