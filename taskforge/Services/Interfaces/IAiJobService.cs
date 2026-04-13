using taskforge.Data.Models.DTO.AI;

namespace taskforge.Services.Interfaces;

public interface IAiJobService
{
    Task<AiJobListResponseDto> GetAdminJobsAsync(string? status, string? type, int page, int pageSize, CancellationToken ct = default);
    Task<AiJobDetailsDto?> GetAdminJobAsync(Guid id, CancellationToken ct = default);
    Task<AiJobDetailsDto> EnqueueAsync(CreateAiJobRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<AiJobDetailsDto> QueueGenerateAssignmentFromTextAsync(AiGenerateAssignmentFromTextRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<AiBatchDetailsDto> QueueGenerateAssignmentBatchAsync(AiGenerateAssignmentBatchRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<IReadOnlyList<AiBatchListItemDto>> GetBatchesAsync(CancellationToken ct = default);
    Task<AiBatchDetailsDto?> GetBatchAsync(Guid id, CancellationToken ct = default);
    Task<AiJobDetailsDto> QueueGenerateAssignmentFromFileAsync(AiGenerateAssignmentFromFileRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<AiJobDetailsDto?> QueueAnalyzeAssignmentAsync(AiAnalyzeAssignmentRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<AiJobDetailsDto?> QueueReviewSubmissionAsync(AiReviewSubmissionRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<AiJobDetailsDto?> QueueReviewUserAsync(AiReviewUserRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<IReadOnlyList<AiSubmissionReviewListItemDto>> GetSubmissionReviewsAsync(Guid? userId, Guid? assignmentId, CancellationToken ct = default);
    Task<IReadOnlyList<AiUserRiskReportListItemDto>> GetUserRiskReportsAsync(Guid? userId, CancellationToken ct = default);
    Task<IReadOnlyList<AiAssignmentInsightListItemDto>> GetAssignmentInsightsAsync(Guid? assignmentId, CancellationToken ct = default);
    Task<AiWorkerPullResponseDto?> PullNextAsync(string workerId, IReadOnlyCollection<string> capabilities, CancellationToken ct = default);
    Task<bool> HeartbeatAsync(Guid jobId, string workerId, CancellationToken ct = default);
    Task<bool> CompleteAsync(Guid jobId, AiWorkerCompleteRequestDto request, CancellationToken ct = default);
    Task<bool> FailAsync(Guid jobId, AiWorkerFailRequestDto request, CancellationToken ct = default);
    Task<IReadOnlyList<AiGeneratedDraftDto>> GetDraftsAsync(CancellationToken ct = default);
    Task<AiGeneratedDraftDto?> GetDraftAsync(Guid id, CancellationToken ct = default);
    Task<bool> ReviewDraftAsync(Guid id, Guid reviewedByUserId, string action, CancellationToken ct = default);
    Task<AiJobDetailsDto?> QueueValidateDraftAsync(Guid draftId, ValidateAiDraftRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<AiJobDetailsDto?> QueueReviseDraftFromChatAsync(AiReviseDraftFromChatRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default);
    Task<PublishAiDraftResultDto?> PublishDraftAsync(Guid id, Guid reviewedByUserId, PublishAiDraftRequestDto request, CancellationToken ct = default);
    Task<bool> DeleteDraftAsync(Guid id, CancellationToken ct = default);
    Task<bool> DeleteBatchAsync(Guid id, CancellationToken ct = default);
    Task<bool> DeleteJobAsync(Guid id, CancellationToken ct = default);
    Task<int> ClearJobsAsync(string? statusFilter, CancellationToken ct = default);
    Task<bool> RetryJobAsync(Guid id, CancellationToken ct = default);
    Task<bool> CancelJobAsync(Guid id, CancellationToken ct = default);
}
