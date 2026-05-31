using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TaskForge.AiAgent.Contracts;

public sealed record ClaimedAgentJob(
    Guid RunId,
    Guid ConversationId,
    string JobType,
    JsonElement Payload,
    string UserText,
    Guid? CourseId,
    string? CourseTitle)
{
    public static ClaimedAgentJob FromJobElement(JsonElement job)
    {
        var id = job.GetGuidOrNull("id", "runId") ?? Guid.Empty;
        var type = job.GetStringOrNull("jobType", "type") ?? "assistant_chat_turn";
        var payload = job.GetPropertyOrDefault("payload");
        if (payload.ValueKind == JsonValueKind.Undefined)
            payload = job;

        var conversationId = payload.GetGuidOrNull("conversationId")
            ?? job.GetGuidOrNull("conversationId")
            ?? Guid.Empty;

        var userText = payload.GetStringOrNull("rawText")
            ?? payload.GetPropertyOrDefault("message").GetStringOrNull("text")
            ?? payload.GetStringOrNull("text")
            ?? string.Empty;

        var courseId = payload.GetGuidOrNull("courseId")
            ?? payload.GetPropertyOrDefault("course").GetGuidOrNull("id");

        var courseTitle = payload.GetPropertyOrDefault("course").GetStringOrNull("title");

        return new ClaimedAgentJob(id, conversationId, type, payload.CloneElement(), userText, courseId, courseTitle);
    }
}

public sealed record AgentArtifact(
    string Type,
    string Title,
    JsonNode Data);

public sealed class AgentResultEnvelope
{
    public string Status { get; set; } = "completed";
    public string ScenarioId { get; set; } = "dotnet_agent";
    public string AssistantMessage { get; set; } = "Готово.";
    public List<AgentArtifact> Artifacts { get; } = new();
    public JsonObject MemoryPatch { get; set; } = new();
    public JsonObject Debug { get; } = new();

    public JsonObject ToJsonObject(bool includeDebug)
    {
        var root = new JsonObject
        {
            ["status"] = Limit(Status, 32, "completed"),
            ["scenarioId"] = Limit(ScenarioId, 256, "dotnet_agent"),
            ["assistantMessage"] = AssistantMessage,
            ["memoryPatch"] = MemoryPatch.DeepClone(),
            ["artifacts"] = new JsonArray(Artifacts.Select(a => new JsonObject
            {
                ["type"] = Limit(a.Type, 80, "artifact"),
                ["title"] = Limit(a.Title, 220, "AI artifact"),
                ["data"] = a.Data.DeepClone()
            }).ToArray<JsonNode?>())
        };

        if (includeDebug)
            root["debug"] = Debug.DeepClone();

        return root;
    }

    private static string Limit(string? value, int maxLength, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

public sealed class AgentStepPayload
{
    public string Kind { get; set; } = "worker";
    public string Status { get; set; } = "completed";
    public string ActionName { get; set; } = "dotnet_agent";
    public string Title { get; set; } = "AI step";
    public string? Summary { get; set; }
    public JsonNode? Data { get; set; }
}

public sealed class TestRunRequest
{
    public Guid? RunId { get; set; }
    public string? WorkerId { get; set; }
    public string Language { get; set; } = "cpp";
    public string Code { get; set; } = string.Empty;
    public List<TestCaseSpec> Tests { get; set; } = new();
}

public sealed class TestCaseSpec
{
    public string Input { get; set; } = string.Empty;
    public string ExpectedOutput { get; set; } = string.Empty;
    public bool IsHidden { get; set; }
}

public sealed class DraftSpec
{
    public string AssignmentType { get; set; } = "code-test";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Language { get; set; } = "cpp";
    public string ReferenceSolution { get; set; } = string.Empty;
    public int Difficulty { get; set; } = 1;
    public int Rating { get; set; } = 10;
    public Guid? CourseId { get; set; }
    public Guid? BeforeAssignmentId { get; set; }
    public Guid? AfterAssignmentId { get; set; }
    public int? SourceTaskIndex { get; set; }
    public List<TestCaseSpec> PublicTests { get; set; } = new();
    public List<TestCaseSpec> HiddenTests { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public JsonObject Extra { get; set; } = new();

    public JsonObject ToArtifactData()
    {
        return new JsonObject
        {
            ["assignmentType"] = AssignmentType,
            ["title"] = Title,
            ["description"] = Description,
            ["language"] = Language,
            ["referenceSolution"] = ReferenceSolution,
            ["difficulty"] = Difficulty,
            ["rating"] = Rating,
            ["courseId"] = CourseId?.ToString(),
            ["beforeAssignmentId"] = BeforeAssignmentId?.ToString(),
            ["afterAssignmentId"] = AfterAssignmentId?.ToString(),
            ["sourceTaskIndex"] = SourceTaskIndex,
            ["tags"] = string.Join(",", Tags.Where(x => !string.IsNullOrWhiteSpace(x))),
            ["publicTests"] = ToJsonArray(PublicTests),
            ["hiddenTests"] = ToJsonArray(HiddenTests),
            ["extra"] = Extra.DeepClone()
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<TestCaseSpec> tests)
    {
        var arr = new JsonArray();
        foreach (var test in tests)
        {
            arr.Add(new JsonObject
            {
                ["input"] = test.Input,
                ["expectedOutput"] = test.ExpectedOutput,
                ["isHidden"] = test.IsHidden
            });
        }
        return arr;
    }
}

public sealed class ApprovalRequestSpec
{
    public string Operation { get; set; } = "unknown";
    public string Reason { get; set; } = string.Empty;
    public JsonObject Payload { get; set; } = new();
}

public static class JsonElementExtensions
{
    public static JsonElement CloneElement(this JsonElement value)
    {
        using var doc = JsonDocument.Parse(value.GetRawText());
        return doc.RootElement.Clone();
    }

    public static JsonElement GetPropertyOrDefault(this JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found))
            return found;
        return default;
    }

    public static string? GetStringOrNull(this JsonElement value, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();
            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.ToString();
        }
        return null;
    }

    public static Guid? GetGuidOrNull(this JsonElement value, params string[] names)
    {
        var raw = value.GetStringOrNull(names);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static int? GetIntOrNull(this JsonElement value, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var direct))
            return direct;

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsedNumber))
                return parsedNumber;
            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsedString))
                return parsedString;
        }
        return null;
    }
}
