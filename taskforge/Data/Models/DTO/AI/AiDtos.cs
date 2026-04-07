using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO.AI;

public sealed class CreateAiJobRequestDto
{
    [Required, MaxLength(100)]
    public string Type { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? TargetEntityType { get; set; }

    public Guid? TargetEntityId { get; set; }

    public Guid? CourseId { get; set; }

    public int Priority { get; set; } = 0;

    public Guid? ParentJobId { get; set; }

    [MaxLength(80)]
    public string? StageCode { get; set; }

    [MaxLength(160)]
    public string? StageLabel { get; set; }

    public int? StageOrder { get; set; }

    public string? InputJson { get; set; }

    public List<AiJobFileDto> Files { get; set; } = new();
}

public sealed class AiJobFileDto
{
    [Required, MaxLength(1024)]
    public string FileKey { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? OriginalName { get; set; }

    [MaxLength(256)]
    public string? MimeType { get; set; }

    [MaxLength(2048)]
    public string? PublicUrl { get; set; }
}

public sealed class AiGenerateAssignmentFromTextRequestDto
{
    [Required]
    public Guid CourseId { get; set; }

    [Required, MaxLength(50)]
    public string AssignmentType { get; set; } = "math";

    [Required]
    public string Prompt { get; set; } = string.Empty;

    public string? SourceText { get; set; }

    [MaxLength(200)]
    public string? TitleHint { get; set; }

    public int Difficulty { get; set; } = 2;

    public int Count { get; set; } = 1;

    public string? Notes { get; set; }

    public int Priority { get; set; } = 20;

    public bool EnableSelfCheck { get; set; } = true;
}

public sealed class AiGenerateAssignmentFromFileRequestDto
{
    [Required]
    public Guid CourseId { get; set; }

    [Required, MaxLength(50)]
    public string AssignmentType { get; set; } = "math";

    [Required]
    public string Prompt { get; set; } = string.Empty;

    [Required, MaxLength(1024)]
    public string FileKey { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? OriginalName { get; set; }

    [MaxLength(256)]
    public string? MimeType { get; set; }

    [MaxLength(2048)]
    public string? PublicUrl { get; set; }

    [MaxLength(200)]
    public string? TitleHint { get; set; }

    public int Difficulty { get; set; } = 2;

    public int Count { get; set; } = 1;

    public string? Notes { get; set; }

    public int Priority { get; set; } = 20;

    public bool EnableSelfCheck { get; set; } = true;
}

public sealed class AiAnalyzeAssignmentRequestDto
{
    [Required]
    public Guid AssignmentId { get; set; }

    public bool IncludeStats { get; set; } = true;

    public bool IncludeAttempts { get; set; } = true;

    public string? Prompt { get; set; }

    public int Priority { get; set; } = 10;
}

public sealed class AiReviewSubmissionRequestDto
{
    [Required, MaxLength(32)]
    public string SourceType { get; set; } = "math";

    [Required]
    public Guid SourceAttemptId { get; set; }

    public string? Prompt { get; set; }

    public int Priority { get; set; } = 10;
}

public sealed class AiReviewUserRequestDto
{
    [Required]
    public Guid UserId { get; set; }

    public bool IncludeSupport { get; set; } = true;

    public bool IncludeMinecraft { get; set; } = true;

    public bool IncludeRecentAttempts { get; set; } = true;

    public string? Prompt { get; set; }

    public int Priority { get; set; } = 10;
}


public sealed class AiGenerateAssignmentBatchRequestDto
{
    [Required]
    public Guid CourseId { get; set; }

    [Required, MaxLength(50)]
    public string AssignmentType { get; set; } = "math";

    [Required]
    public string Prompt { get; set; } = string.Empty;

    public int Count { get; set; } = 5;

    [MaxLength(80)]
    public string? Mode { get; set; } = "topic-pack";

    public int Difficulty { get; set; } = 2;

    public string? Notes { get; set; }

    public int Priority { get; set; } = 20;
}

public class AiBatchListItemDto
{
    public Guid Id { get; set; }
    public Guid? CourseId { get; set; }
    public string AssignmentType { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int RequestedCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CurrentStage { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int ItemsCount { get; set; }
    public int ReadyItemsCount { get; set; }
}

public sealed class AiBatchItemDto
{
    public Guid Id { get; set; }
    public int Index { get; set; }
    public string? TargetSkill { get; set; }
    public int DifficultyTarget { get; set; }
    public string? MicroGoal { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? DraftId { get; set; }
    public int RepairCount { get; set; }
    public string? BriefJson { get; set; }
    public string? BriefReviewJson { get; set; }
    public string? ContextReviewJson { get; set; }
    public string? DecisionLogJson { get; set; }
    public string? PlannerSignalsJson { get; set; }
    public string? HistoricalSlotPriorsJson { get; set; }
    public string? AntiPatternFlagsJson { get; set; }
    public string? ReplanHistoryJson { get; set; }
    public string? ReferencePackJson { get; set; }
    public string? StylePackJson { get; set; }
    public string? PolicyPackJson { get; set; }
    public string? NegativePackJson { get; set; }
    public string? ExemplarPackJson { get; set; }
    public string? ReferenceSignalsJson { get; set; }
    public string? ScorecardJson { get; set; }
}

public sealed class AiBatchDetailsDto : AiBatchListItemDto
{
    public string? CanonicalRequestJson { get; set; }
    public string? CourseProfileJson { get; set; }
    public string? GapAnalysisJson { get; set; }
    public string? AssignmentOntologyJson { get; set; }
    public string? ExemplarSignalsJson { get; set; }
    public string? NegativeMemoryJson { get; set; }
    public string? CoverageJson { get; set; }
    public string? PlanJson { get; set; }
    public string? SummaryJson { get; set; }
    public string? DecisionSummaryJson { get; set; }
    public string? BatchReviewJson { get; set; }
    public string? ReviewLedgerJson { get; set; }
    public string? StudentJourneyJson { get; set; }
    public string? PublicationAuditJson { get; set; }
    public string? PublishPackJson { get; set; }
    public string? QualityLedgerJson { get; set; }
    public string? ExportManifestJson { get; set; }
    public string? PlannerFeedbackJson { get; set; }
    public string? HistoricalPlannerPriorsJson { get; set; }
    public string? PositiveMemoryJson { get; set; }
    public string? BatchMemoryJson { get; set; }
    public string? InstitutionalMemoryJson { get; set; }
    public string? AntiPatternMemoryJson { get; set; }
    public string? ReplanLedgerJson { get; set; }
    public string? DecisionLogDigestJson { get; set; }
    public string? FeedbackLoopStateJson { get; set; }
    public List<AiBatchItemDto> Items { get; set; } = new();
}

public class AiJobListItemDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string? ModelName { get; set; }
    public string? WorkerId { get; set; }
    public string? CreatedByDisplayName { get; set; }
    public string? TargetEntityType { get; set; }
    public Guid? TargetEntityId { get; set; }
    public Guid? CourseId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public Guid? ParentJobId { get; set; }
    public string? StageCode { get; set; }
    public string? StageLabel { get; set; }
    public int? StageOrder { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorText { get; set; }
    public int FilesCount { get; set; }
}

public sealed class AiJobDetailsDto : AiJobListItemDto
{
    public List<AiArtifactDto> Artifacts { get; set; } = new();
    public string? InputJson { get; set; }
    public string? ResultJson { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public List<AiJobFileDto> Files { get; set; } = new();
}

public sealed class AiGeneratedDraftDto
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? CourseId { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? BatchItemId { get; set; }
    public string AssignmentType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DraftJson { get; set; } = "{}";
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
}

public sealed class AiSubmissionReviewListItemDto
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? AssignmentId { get; set; }
    public string? SourceType { get; set; }
    public Guid? SourceAttemptId { get; set; }
    public string Verdict { get; set; } = string.Empty;
    public double? Score { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? SignalsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class AiUserRiskReportListItemDto
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public Guid UserId { get; set; }
    public string RiskLevel { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? SignalsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public sealed class AiAssignmentInsightListItemDto
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public Guid AssignmentId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? SuggestionsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class ReviewAiDraftRequestDto
{
    [Required, MaxLength(32)]
    public string Action { get; set; } = "approve";
}



public sealed class ValidateAiDraftRequestDto
{
    public bool UsePythonSelfCheck { get; set; } = true;

    public string? Prompt { get; set; }

    public int Priority { get; set; } = 15;
}

public sealed class PublishAiDraftRequestDto
{
    public Guid? CourseId { get; set; }

    [MaxLength(200)]
    public string? TitleOverride { get; set; }

    public int? Difficulty { get; set; }
    public int? Rating { get; set; }

    public string? Tags { get; set; }

    public int? Sort { get; set; }

    public bool ForceWithoutPassedSelfCheck { get; set; } = false;
}


public sealed class AiArtifactDto
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? DraftId { get; set; }
    public string ArtifactType { get; set; } = string.Empty;
    public string? StageCode { get; set; }
    public string? Status { get; set; }
    public string? PayloadJson { get; set; }
    public string? ModelName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class PublishAiDraftResultDto
{
    public Guid DraftId { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid CourseId { get; set; }
    public string AssignmentType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public sealed class AiJobListResponseDto
{
    public List<AiJobListItemDto> Items { get; set; } = new();
    public int Total { get; set; }
}

public sealed class AiWorkerPullRequestDto
{
    [Required, MaxLength(128)]
    public string WorkerId { get; set; } = string.Empty;

    public List<string> Capabilities { get; set; } = new();
}

public sealed class AiWorkerPullResponseDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string? ModelName { get; set; }
    public string? TargetEntityType { get; set; }
    public Guid? TargetEntityId { get; set; }
    public Guid? CourseId { get; set; }
    public Guid? ParentJobId { get; set; }
    public string? StageCode { get; set; }
    public string? StageLabel { get; set; }
    public int? StageOrder { get; set; }
    public string? InputJson { get; set; }
    public List<AiJobFileDto> Files { get; set; } = new();
}

public sealed class AiWorkerHeartbeatRequestDto
{
    [Required, MaxLength(128)]
    public string WorkerId { get; set; } = string.Empty;
}

public sealed class AiWorkerCompleteRequestDto
{
    [Required, MaxLength(128)]
    public string WorkerId { get; set; } = string.Empty;

    public string? ModelName { get; set; }

    public string? ResultJson { get; set; }
}

public sealed class AiWorkerFailRequestDto
{
    [Required, MaxLength(128)]
    public string WorkerId { get; set; } = string.Empty;

    [Required]
    public string ErrorText { get; set; } = string.Empty;

    public bool Retryable { get; set; } = false;

    public int RetryDelaySeconds { get; set; } = 120;
}


public sealed class AiFoundryChatMessageDto
{
    [Required, MaxLength(32)]
    public string Role { get; set; } = "user";

    [Required]
    public string Content { get; set; } = string.Empty;
}

public sealed class AiFoundryChatResolveRequestDto
{
    public Guid? CourseId { get; set; }

    [MaxLength(50)]
    public string? AssignmentType { get; set; }

    public int? Difficulty { get; set; }

    public int? Count { get; set; }

    [MaxLength(80)]
    public string? Mode { get; set; }

    public List<AiFoundryChatMessageDto> Messages { get; set; } = new();
}

public sealed class AiFoundryChatPlanDto
{
    public string Prompt { get; set; } = string.Empty;
    public string AssignmentType { get; set; } = "code-test";
    public int Difficulty { get; set; } = 3;
    public int Count { get; set; } = 5;
    public string Mode { get; set; } = "topic-pack";
    public string? Notes { get; set; }
    public List<string> Goals { get; set; } = new();
    public List<string> Constraints { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class AiFoundryChatResolveResponseDto
{
    public string SessionTitle { get; set; } = string.Empty;
    public string AssistantMessage { get; set; } = string.Empty;
    public AiFoundryChatPlanDto? Plan { get; set; }
}
