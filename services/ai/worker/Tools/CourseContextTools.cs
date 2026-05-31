using System.ComponentModel;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Tools;

public sealed class CourseContextTools
{
    private readonly AgentRunContextAccessor _contextAccessor;
    private readonly PromptContextComposer _composer;

    public CourseContextTools(AgentRunContextAccessor contextAccessor, PromptContextComposer composer)
    {
        _contextAccessor = contextAccessor;
        _composer = composer;
    }

    [Description("Get the current TaskForge AI run context: user request, selected course, course outline, focus assignments, attachments, recent messages and memory from backend payload.")]
    public Task<string> GetCurrentRunContextAsync()
    {
        var context = _contextAccessor.Current;
        return Task.FromResult(_composer.ComposeRunPrompt(context.Job, "tool:get-current-run-context"));
    }

    [Description("Search assignments from the already loaded backend payload by a short query. Use this before generating tasks or auditing a concept gap.")]
    public Task<JsonObject> SearchAssignmentsAsync([Description("Search query in Russian or English, e.g. 'циклы', 'arrays', 'LINQ'.")] string query)
    {
        var job = _contextAccessor.Current.Job;
        var raw = job.Payload.GetRawText();
        var queryLower = (query ?? string.Empty).Trim().ToLowerInvariant();
        var result = new JsonArray();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            AddMatches(doc.RootElement, queryLower, result, 0);
        }
        catch
        {
            // The tool is intentionally best-effort; the agent can still ask for current context.
        }

        return Task.FromResult(new JsonObject
        {
            ["query"] = query,
            ["matches"] = result,
            ["note"] = "Search is performed over the compact payload already provided by TaskForge backend."
        });
    }

    [Description("Analyze the selected course for educational gaps, sudden difficulty jumps, weak test coverage and missing bridge tasks. Returns a structured audit skeleton based on backend payload.")]
    public Task<JsonObject> AnalyzeCourseGapAsync([Description("Target topic/concept to inspect. Leave empty for whole course audit.")] string? targetConcept = null)
    {
        var job = _contextAccessor.Current.Job;
        var artifact = new JsonObject
        {
            ["selectedCourseId"] = job.CourseId?.ToString(),
            ["targetConcept"] = targetConcept,
            ["auditMode"] = string.IsNullOrWhiteSpace(targetConcept) ? "whole_course" : "targeted_concept",
            ["checks"] = new JsonArray("sequence", "difficulty_jumps", "missing_bridge_tasks", "weak_tests", "style_consistency"),
            ["recommendation"] = "Use CourseAuditWorkflow for final findings; this tool only exposes structured context to the coordinator."
        };
        return Task.FromResult(artifact);
    }

    private static void AddMatches(System.Text.Json.JsonElement element, string query, JsonArray result, int depth)
    {
        if (result.Count >= 20 || depth > 6) return;

        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var title = element.GetStringOrNull("title", "name");
            var description = element.GetStringOrNull("description", "descriptionPreview", "body", "condition");
            var tags = element.GetStringOrNull("tags");
            var haystack = string.Join(" ", title, description, tags).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(query) && haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new JsonObject
                {
                    ["id"] = element.GetStringOrNull("id"),
                    ["courseId"] = element.GetStringOrNull("courseId"),
                    ["title"] = title,
                    ["type"] = element.GetStringOrNull("type", "assignmentType"),
                    ["difficulty"] = element.GetStringOrNull("difficulty"),
                    ["tags"] = tags,
                    ["descriptionPreview"] = description is { Length: > 300 } ? description[..300] : description
                });
            }

            foreach (var prop in element.EnumerateObject())
                AddMatches(prop.Value, query, result, depth + 1);
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AddMatches(item, query, result, depth + 1);
        }
    }
}
