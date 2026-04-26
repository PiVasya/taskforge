using System.Text.Json;

namespace taskforge.Data.Models.DTO.Agent
{
    public sealed class AgentCreateConversationRequest
    {
        public Guid? CourseId { get; set; }
        public Guid? AssignmentId { get; set; }
        public Guid? SupportTicketId { get; set; }
        public string? Title { get; set; }
        public string? Mode { get; set; }
        public string? FirstMessage { get; set; }
    }

    public sealed class AgentSendMessageRequest
    {
        public string Text { get; set; } = string.Empty;
        public string? ClientMessageId { get; set; }
    }

    public sealed class AgentConversationDto
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public Guid? CourseId { get; set; }
        public Guid? AssignmentId { get; set; }
        public Guid? SupportTicketId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Mode { get; set; } = "course-assistant";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public string? LastMessageText { get; set; }
        public string? LastRunStatus { get; set; }
    }

    public sealed class AgentConversationDetailsDto
    {
        public AgentConversationDto Conversation { get; set; } = new();
        public List<AgentMessageDto> Messages { get; set; } = new();
        public List<AgentRunDto> Runs { get; set; } = new();
    }

    public sealed class AgentMessageDto
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public Guid? RunId { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public JsonElement? Data { get; set; }
        public string? ClientMessageId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public sealed class AgentRunDto
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ScenarioId { get; set; }
        public string? WorkerId { get; set; }
        public int Attempt { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public JsonElement? Result { get; set; }
        public JsonElement? Error { get; set; }
        public List<AgentStepDto> Steps { get; set; } = new();
        public List<AgentRunArtifactDto> Artifacts { get; set; } = new();
    }

    public sealed class AgentStepDto
    {
        public Guid Id { get; set; }
        public Guid RunId { get; set; }
        public int Seq { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? ActionName { get; set; }
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public JsonElement? Input { get; set; }
        public JsonElement? Output { get; set; }
        public JsonElement? Error { get; set; }
        public bool IsVisibleToUser { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
    }

    public sealed class AgentRunArtifactDto
    {
        public Guid Id { get; set; }
        public Guid RunId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public JsonElement? Data { get; set; }
        public string? StorageKey { get; set; }
        public string? ContentHash { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public sealed class AgentCancelRunRequest
    {
        public string? Reason { get; set; }
    }

    public sealed class AgentPolishGeneratedTaskRequest
    {
        public Guid? SourceMessageId { get; set; }
        public Guid? SourceRunId { get; set; }
        public Guid? SourceArtifactId { get; set; }
        public int? TaskIndex { get; set; }
        public Guid? CourseId { get; set; }
        public Guid? BeforeAssignmentId { get; set; }
        public Guid? AfterAssignmentId { get; set; }
        public JsonElement Task { get; set; }
        public string? Note { get; set; }
    }

    public sealed class AgentPolishGeneratedTaskBatchRequest
    {
        public List<AgentPolishGeneratedTaskRequest> Tasks { get; set; } = new();
        public string? Note { get; set; }
        public bool Parallelize { get; set; } = true;
    }

    public sealed class AgentPublishDraftRequest
    {
        public bool Publish { get; set; } = true;
    }

    public sealed class InternalAgentClaimNextRequest
    {
        public string? WorkerId { get; set; }
    }

    public sealed class InternalAgentHeartbeatRequest
    {
        public string? WorkerId { get; set; }
    }

    public sealed class InternalAgentAppendStepRequest
    {
        public string? WorkerId { get; set; }
        public JsonElement Step { get; set; }
    }

    public sealed class InternalAgentCompleteRunRequest
    {
        public string? WorkerId { get; set; }
        public JsonElement Result { get; set; }
    }

    public sealed class InternalAgentFailRunRequest
    {
        public string? WorkerId { get; set; }
        public JsonElement Error { get; set; }
    }
}
