using taskforge.Data.Models.DTO.TaskMaths;

namespace taskforge.Services.Interfaces
{
    public interface ITaskMathService
    {
        Task<TaskMathStartResponseDto> StartAsync(Guid assignmentId, Guid userId, CancellationToken ct);
        Task<TaskMathSubmitResultDto> SubmitAsync(Guid assignmentId, Guid userId, TaskMathSubmitRequestDto request, CancellationToken ct);
        Task<TaskMathEditDto> GetEditAsync(Guid assignmentId, Guid userId, CancellationToken ct);
        Task SaveEditAsync(Guid assignmentId, Guid userId, TaskMathEditDto dto, CancellationToken ct);
    }
}
