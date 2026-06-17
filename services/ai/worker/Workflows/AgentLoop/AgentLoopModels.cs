using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed class AgentLoopState
{
    public required ClaimedAgentJob Job { get; init; }
    public string Status { get; set; } = "running";
    public string ScenarioId { get; set; } = "adaptive_agent_loop";
    public string? SelectedWorkflow { get; set; }
    public string? FinalMessage { get; set; }
    public AgentResultEnvelope? DelegatedResult { get; set; }
    public JsonObject WorkingMemory { get; } = new();
    public List<AgentLoopTraceEntry> Trace { get; } = new();
    public List<string> Notes { get; } = new();
    public bool LoadedContext { get; set; }
    public bool Finished { get; set; }
    public int StepNumber { get; set; }

    public JsonObject ToJsonObject(int maxCharacters = 64000)
    {
        var root = new JsonObject
        {
            ["status"] = Status,
            ["scenarioId"] = ScenarioId,
            ["selectedWorkflow"] = SelectedWorkflow,
            ["loadedContext"] = LoadedContext,
            ["finished"] = Finished,
            ["stepNumber"] = StepNumber,
            ["hasDelegatedResult"] = DelegatedResult is not null,
            ["hasPendingPatchSet"] = WorkingMemory.ContainsKey("pendingPatchSet"),
            ["finalMessagePreview"] = Preview(FinalMessage, 1200),
            ["delegatedResult"] = DelegatedResult is null ? null : new JsonObject
            {
                ["status"] = DelegatedResult.Status,
                ["scenarioId"] = DelegatedResult.ScenarioId,
                ["assistantMessagePreview"] = Preview(DelegatedResult.AssistantMessage, 1200),
                ["artifactCount"] = DelegatedResult.Artifacts.Count,
                ["artifactTypes"] = new JsonArray(DelegatedResult.Artifacts.Select(a => JsonValue.Create(a.Type)).ToArray<JsonNode?>())
            },
            ["job"] = new JsonObject
            {
                ["runId"] = Job.RunId.ToString(),
                ["conversationId"] = Job.ConversationId.ToString(),
                ["jobType"] = Job.JobType,
                ["userText"] = Job.UserText,
                ["courseId"] = Job.CourseId?.ToString(),
                ["assignmentId"] = Job.AssignmentId?.ToString(),
                ["courseTitle"] = Job.CourseTitle
            },
            ["workingMemory"] = WorkingMemory.DeepClone(),
            ["notes"] = new JsonArray(Notes.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
            ["trace"] = new JsonArray(Trace.Select(x => x.ToJsonObject()).ToArray<JsonNode?>())
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (json.Length <= maxCharacters)
            return root;

        root["trace"] = new JsonArray(Trace.TakeLast(8).Select(x => x.ToJsonObject()).ToArray<JsonNode?>());
        root["truncated"] = true;
        root["truncationReason"] = $"Agent state exceeded {maxCharacters} characters; kept full workingMemory and last trace entries.";
        return root;
    }

    private static string? Preview(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}

public sealed class AgentLoopTraceEntry
{
    public int Step { get; set; }
    public int BatchIndex { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ReasonSummary { get; set; } = string.Empty;
    public string Status { get; set; } = "completed";
    public JsonObject Args { get; set; } = new();
    public JsonObject Observation { get; set; } = new();
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public JsonObject ToJsonObject() => new()
    {
        ["step"] = Step,
        ["batchIndex"] = BatchIndex,
        ["action"] = Action,
        ["reasonSummary"] = ReasonSummary,
        ["status"] = Status,
        ["args"] = Args.DeepClone(),
        ["observation"] = Observation.DeepClone(),
        ["createdAtUtc"] = CreatedAtUtc.ToString("O")
    };
}

public sealed class AgentLoopActionCall
{
    public string Action { get; set; } = "inspect_context";
    public string ReasonSummary { get; set; } = "Нужно понять запрос и доступный контекст.";
    public JsonObject Args { get; set; } = new();

    public JsonObject ToJsonObject() => new()
    {
        ["action"] = Action,
        ["reasonSummary"] = ReasonSummary,
        ["args"] = Args.DeepClone()
    };
}

public sealed class AgentLoopDecision
{
    public string Action { get; set; } = "inspect_context";
    public string ReasonSummary { get; set; } = "Нужно понять запрос и доступный контекст.";
    public JsonObject Args { get; set; } = new();
    public List<AgentLoopActionCall> Actions { get; } = new();

    public IReadOnlyList<AgentLoopActionCall> GetActionCalls()
    {
        if (Actions.Count > 0) return Actions;
        return new[]
        {
            new AgentLoopActionCall
            {
                Action = Action,
                ReasonSummary = ReasonSummary,
                Args = Args.DeepClone().AsObject()
            }
        };
    }

    public static AgentLoopDecision Fallback(string action, string reason) => new()
    {
        Action = action,
        ReasonSummary = reason,
        Args = new JsonObject()
    };

    public static AgentLoopDecision FallbackBatch(IEnumerable<(string Action, string Reason, JsonObject? Args)> actions)
    {
        var decision = new AgentLoopDecision
        {
            Action = "batch",
            ReasonSummary = "Система выбрала безопасный пакет действий.",
            Args = new JsonObject()
        };
        foreach (var item in actions)
        {
            decision.Actions.Add(new AgentLoopActionCall
            {
                Action = item.Action,
                ReasonSummary = item.Reason,
                Args = item.Args?.DeepClone().AsObject() ?? new JsonObject()
            });
        }
        return decision;
    }
}
