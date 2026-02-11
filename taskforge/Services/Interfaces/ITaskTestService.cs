using taskforge.Data.Models.DTO.TaskTests;

namespace taskforge.Services.Interfaces
{
    public interface ITaskTestService
    {
        Task<TaskTestStartResponseDto> StartAsync(Guid assignmentId, Guid userId, CancellationToken ct);
        Task<TaskTestSubmitResultDto> SubmitAsync(Guid assignmentId, Guid userId, TaskTestSubmitRequestDto request, CancellationToken ct);

        // attempts (review)
        Task<List<TaskTestAttemptListItemDto>> GetAttemptsAsync(
            Guid userId,
            Guid? courseId,
            Guid? assignmentId,
            int? days,
            int skip,
            int take,
            CancellationToken ct);

        Task<TaskTestAttemptReviewDto?> GetAttemptReviewAsync(
            Guid userId,
            Guid attemptId,
            bool isAdmin,
            CancellationToken ct);

        Task DeleteAttemptAsync(Guid attemptId, CancellationToken ct);

        // editor
        Task<TaskTestEditDto> GetEditAsync(Guid assignmentId, Guid userId, CancellationToken ct);
        Task SaveEditAsync(Guid assignmentId, Guid userId, TaskTestEditDto dto, CancellationToken ct);
    }
}
