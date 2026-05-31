using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Workflows.Executors;

namespace TaskForge.AiAgent.Workflows;

public sealed class WorkflowState
{
    public required ClaimedAgentJob Job { get; init; }
    public string WorkflowName { get; init; } = "open_chat";
    public string UserText => Job.UserText;
    public JsonObject Data { get; } = new();
    public List<AgentArtifact> Artifacts { get; } = new();
    public List<string> Notes { get; } = new();
    public DraftSpec? Draft { get; set; }
    public JsonObject TeacherPreferences { get; set; } = new();
    public JsonObject MemoryPatch { get; } = new();
    public CourseSkillBridgeContext? CourseSkillBridge { get; set; }
    public bool RequiresApproval { get; set; }
    public string AssistantMessage { get; set; } = string.Empty;
    public string ScenarioId { get; set; } = "dotnet_agent";
}

public interface ITaskForgeWorkflow
{
    string Name { get; }
    int Priority { get; }
    bool CanHandle(ClaimedAgentJob job);
    Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken);
}
