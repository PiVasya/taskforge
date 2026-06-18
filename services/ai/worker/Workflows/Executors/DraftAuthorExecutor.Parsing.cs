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
    private List<DraftSpec> ParseDrafts(string text, ClaimedAgentJob job)
    {
        var result = new List<DraftSpec>();
        try
        {
            var json = ExtractJson(text);
            if (json == null) return result;
            var root = JsonNode.Parse(json);
            if (root is JsonArray arr)
            {
                foreach (var item in arr.OfType<JsonObject>())
                    result.Add(ParseDraftObject(item, job, text));
            }
            else if (root is JsonObject obj)
            {
                if (obj["drafts"] is JsonArray drafts)
                {
                    foreach (var item in drafts.OfType<JsonObject>())
                        result.Add(ParseDraftObject(item, job, text));
                }
                else
                {
                    result.Add(ParseDraftObject(obj, job, text));
                }
            }
        }
        catch
        {
            // fallback below
        }
        return result.Where(IsDraftUseful).ToList();
    }

    private DraftSpec ParseDraftObject(JsonObject node, ClaimedAgentJob job, string rawText)
    {
        var extra = BuildDraftExtra(node, rawText);
        return new DraftSpec
        {
            AssignmentType = node["assignmentType"]?.ToString() ?? node["assignment_type"]?.ToString() ?? "code-test",
            Title = node["title"]?.ToString() ?? "Черновик задания",
            Description = node["description"]?.ToString() ?? node["condition"]?.ToString() ?? "Описание задания не было заполнено моделью.",
            Language = NormalizeLanguage(node["language"]?.ToString() ?? _options.DefaultLanguage),
            ReferenceSolution = node["referenceSolution"]?.ToString() ?? node["solution"]?.ToString() ?? string.Empty,
            Difficulty = int.TryParse(node["difficulty"]?.ToString(), out var d) ? System.Math.Clamp(d, 1, 3) : 1,
            Rating = int.TryParse(node["rating"]?.ToString(), out var r) ? System.Math.Max(1, r) : 10,
            CourseId = job.CourseId,
            SourceTaskIndex = int.TryParse(node["sourceTaskIndex"]?.ToString() ?? node["source_task_index"]?.ToString(), out var idx) ? idx : null,
            Tags = ReadStringArray(node["tags"]).DefaultIfEmpty("AI").ToList(),
            PublicTests = ReadTests(node["publicTests"] ?? node["tests"], false),
            HiddenTests = ReadTests(node["hiddenTests"], true),
            TestSpec = ReadTestSpec(node),
            MathSpec = ReadMathSpec(node),
            Extra = extra
        };
    }

    private static JsonObject? ReadTestSpec(JsonObject node)
    {
        if (node["testSpec"] is JsonObject spec)
            return spec.DeepClone() as JsonObject;
        if (node["test"] is JsonObject test)
            return test.DeepClone() as JsonObject;
        if (node["taskTest"] is JsonObject taskTest)
            return taskTest.DeepClone() as JsonObject;
        if (node["questions"] is JsonArray questions)
        {
            return new JsonObject
            {
                ["settings"] = new JsonObject(),
                ["questions"] = questions.DeepClone()
            };
        }
        return null;
    }

    private static JsonObject? ReadMathSpec(JsonObject node)
    {
        if (node["mathSpec"] is JsonObject spec)
            return spec.DeepClone() as JsonObject;
        if (node["math"] is JsonObject math)
            return math.DeepClone() as JsonObject;
        if (node["taskMath"] is JsonObject taskMath)
            return taskMath.DeepClone() as JsonObject;
        if (node["blocks"] is JsonArray blocks)
        {
            return new JsonObject
            {
                ["settings"] = new JsonObject(),
                ["blocks"] = blocks.DeepClone()
            };
        }
        return null;
    }

    private static JsonObject BuildDraftExtra(JsonObject node, string rawText)
    {
        JsonObject extra;
        if (node["extra"] is JsonObject modelExtra)
            extra = modelExtra.DeepClone() as JsonObject ?? new JsonObject();
        else
            extra = new JsonObject();

        CopySkillArray(node, extra, "assumedSkills");
        CopySkillArray(node, extra, "introducedSkills");
        CopySkillArray(node, extra, "targetSkills");
        CopySkillArray(node, extra, "missingBridgeSkills");
        CopySkillArray(node, extra, "missingSkills", "missingBridgeSkills");

        if (node["skillBridgeReason"] is not null && extra["skillBridgeReason"] is null)
            extra["skillBridgeReason"] = node["skillBridgeReason"]!.ToString();

        // Keep raw LLM text out of draft metadata. It is noisy, can leak internal
        // prompt/repair text into служебные материалы, and may confuse the model critic
        // into critiquing stale rawModelDraft content instead of the normalized
        // student-facing assignment.
        return extra;
    }

    private static void CopySkillArray(JsonObject source, JsonObject target, string sourceName, string? targetName = null)
    {
        targetName ??= sourceName;
        if (target[targetName] is not null || source[sourceName] is null) return;
        var values = ReadStringArray(source[sourceName]).ToList();
        if (values.Count == 0) return;
        var arr = new JsonArray();
        foreach (var value in values) arr.Add(value);
        target[targetName] = arr;
    }

    private static bool IsDraftUseful(DraftSpec draft)
        => !string.IsNullOrWhiteSpace(draft.Title) && !string.IsNullOrWhiteSpace(draft.Description);

    public static bool LooksLikeMultipleDraftRequest(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("задачки")
               || t.Contains("обучалки")
               || t.Contains("несколько")
               || t.Contains("серия")
               || t.Contains("набор")
               || t.Contains("лестниц")
               || t.Contains("guided ladder")
               || t.Contains("learning bridge")
               || t.Contains("bridge task")
               || CourseSkillAnalyzer.LooksLikeLearningBridgeRequest(text);
    }

    private static string? ExtractJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var first = t.IndexOf('\n');
            var last = t.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first) t = t[(first + 1)..last].Trim();
        }

        var balancedObject = ExtractFirstBalanced(t, '{', '}');
        var balancedArray = ExtractFirstBalanced(t, '[', ']');
        if (balancedObject != null && (balancedArray == null || t.IndexOf('{') < t.IndexOf('['))) return balancedObject;
        if (balancedArray != null) return balancedArray;

        var objectStart = t.IndexOf('{');
        var objectEnd = t.LastIndexOf('}');
        var arrayStart = t.IndexOf('[');
        var arrayEnd = t.LastIndexOf(']');
        if (objectStart >= 0 && objectEnd > objectStart && (arrayStart < 0 || objectStart < arrayStart)) return t[objectStart..(objectEnd + 1)];
        if (arrayStart >= 0 && arrayEnd > arrayStart) return t[arrayStart..(arrayEnd + 1)];
        return null;
    }

    private static string? ExtractFirstBalanced(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (inString)
            {
                if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"')
            {
                inString = true;
                continue;
            }
            if (ch == open) depth++;
            else if (ch == close)
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
                if (depth < 0) return null;
            }
        }
        return null;
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node == null) return null;
        return int.TryParse(node.ToString(), out var value) ? value : null;
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

    private static List<TestCaseSpec> ReadTests(JsonNode? node, bool hidden)
    {
        var result = new List<TestCaseSpec>();
        if (node is not JsonArray arr) return result;
        foreach (var item in arr.OfType<JsonObject>())
        {
            result.Add(new TestCaseSpec
            {
                Input = item["input"]?.ToString() ?? string.Empty,
                ExpectedOutput = item["expectedOutput"]?.ToString() ?? item["output"]?.ToString() ?? string.Empty,
                IsHidden = hidden || bool.TryParse(item["isHidden"]?.ToString(), out var isHidden) && isHidden
            });
        }
        return result;
    }

    private static string NormalizeLanguage(string? value)
    {
        var text = (value ?? "cpp").Trim().ToLowerInvariant();
        return text switch
        {
            "c#" or "csharp" or "cs" or "sharp" or "с#" or "си#" => "csharp",
            "py" or "python" or "python3" => "python",
            "js" or "node" or "nodejs" or "javascript" => "javascript",
            "pas" or "pascal" => "pascal",
            "java" => "java",
            "ru" => "cpp",
            _ => string.IsNullOrWhiteSpace(text) ? "cpp" : text
        };
    }
}
