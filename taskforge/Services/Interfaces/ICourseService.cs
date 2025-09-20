using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICourseService
    {
        Task<Guid> CreateAsync(CreateCourseRequest req, Guid ownerId);
        Task<IReadOnlyList<CourseListItemDto>> GetListAsync(Guid currentUserId);
        Task<CourseDetailsDto?> GetDetailsAsync(Guid courseId, Guid currentUserId);
    }
}
