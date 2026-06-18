using System.ComponentModel;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Tools;

public sealed class AssignmentDraftTools
{
    private readonly AgentRunContextAccessor _contextAccessor;

    public AssignmentDraftTools(AgentRunContextAccessor contextAccessor) => _contextAccessor = contextAccessor;

    [Description("Build a strongly typed code-test assignment draft blueprint for TaskForge. It does not save anything; it only creates a draft artifact.")]
    public Task<JsonObject> BuildCodeAssignmentDraftAsync(
        [Description("Short assignment title.")] string title,
        [Description("Clear assignment statement in Russian.")] string description,
        [Description("Programming language: cpp, csharp, java, javascript, pascal, python.")] string language,
        [Description("Reference solution code that should pass all tests.")] string referenceSolution,
        [Description("Difficulty 1..3.")] int difficulty = 1)
    {
        var job = _contextAccessor.Current.Job;
        var draft = new DraftSpec
        {
            AssignmentType = "code-test",
            Title = title.Trim(),
            Description = description.Trim(),
            Language = NormalizeLanguage(language),
            ReferenceSolution = referenceSolution,
            Difficulty = System.Math.Clamp(difficulty, 1, 3),
            Rating = System.Math.Clamp(difficulty, 1, 3) * 10,
            CourseId = job.CourseId,
            Tags = new List<string> { "AI", "черновик", "code-test" },
            PublicTests = new List<TestCaseSpec>
            {
                new() { Input = "", ExpectedOutput = "", IsHidden = false }
            },
            HiddenTests = new List<TestCaseSpec>
            {
                new() { Input = "", ExpectedOutput = "", IsHidden = true }
            }
        };

        draft.Extra["createdBy"] = "TaskForge .NET Agent";
        draft.Extra["qualityNote"] = "Tests are placeholders unless the workflow/agent fills concrete cases.";
        return Task.FromResult(draft.ToArtifactData());
    }

    [Description("Build a strongly typed math assignment draft blueprint. It does not save anything; it only creates a draft artifact.")]
    public Task<JsonObject> BuildMathAssignmentDraftAsync(
        [Description("Short assignment title.")] string title,
        [Description("Assignment statement in Russian.")] string description,
        [Description("Difficulty 1..3.")] int difficulty = 1)
    {
        var job = _contextAccessor.Current.Job;
        var data = new JsonObject
        {
            ["assignmentType"] = "math",
            ["title"] = title.Trim(),
            ["description"] = description.Trim(),
            ["difficulty"] = System.Math.Clamp(difficulty, 1, 3),
            ["rating"] = System.Math.Clamp(difficulty, 1, 3) * 10,
            ["courseId"] = job.CourseId?.ToString(),
            ["tags"] = "AI,черновик,math",
            ["mathBlocks"] = new JsonArray(new JsonObject
            {
                ["prompt"] = description.Trim(),
                ["answer"] = "Эталонный ответ должен быть сформирован агентом перед сохранением draft.",
                ["explanation"] = "Подробное объяснение решения должно быть добавлено агентом или преподавателем."
            })
        };
        return Task.FromResult(data);
    }

    [Description("Normalize and enrich a draft artifact. Use this after model-generated JSON to enforce TaskForge fields.")]
    public Task<JsonObject> NormalizeDraftAsync(JsonObject draft)
    {
        draft["assignmentType"] ??= "code-test";
        draft["difficulty"] ??= 1;
        draft["rating"] ??= 10;
        draft["language"] = NormalizeLanguage(draft["language"]?.ToString() ?? "cpp");
        draft["tags"] ??= "AI,черновик";
        draft["courseId"] ??= _contextAccessor.Current.Job.CourseId?.ToString();
        return Task.FromResult(draft);
    }

    private static string NormalizeLanguage(string? language)
    {
        var value = (language ?? "cpp").Trim().ToLowerInvariant();
        return value switch
        {
            "c++" or "cpp" => "cpp",
            "c#" or "cs" or "csharp" => "csharp",
            "js" or "javascript" => "javascript",
            "py" or "python" => "python",
            "java" => "java",
            "pascal" => "pascal",
            "ru" => "cpp",
            _ => string.IsNullOrWhiteSpace(value) ? "cpp" : value
        };
    }
}
