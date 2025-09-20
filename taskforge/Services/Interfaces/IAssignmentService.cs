using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface IAssignmentService
    {
        Task<Guid> CreateAsync(Guid courseId, CreateAssignmentRequest req, Guid authorId);
        Task<IReadOnlyList<AssignmentListItemDto>> GetByCourseAsync(Guid courseId, Guid currentUserId);
        Task<AssignmentDetailsDto?> GetDetailsAsync(Guid assignmentId, Guid currentUserId);
    }
}
