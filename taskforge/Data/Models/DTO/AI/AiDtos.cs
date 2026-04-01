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
    public int FilesCount { get; set; }
}

public sealed class AiJobDetailsDto : AiJobListItemDto
{
    public string? InputJson { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorText { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public int RetryCount { get; set; }
    public List<AiJobFileDto> Files { get; set; } = new();
}

public sealed class AiGeneratedDraftDto
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? CourseId { get; set; }
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
