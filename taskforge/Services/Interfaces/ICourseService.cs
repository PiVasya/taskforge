using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICourseService
    {
        Task<Guid> CreateAsync(CreateCourseRequest req, Guid ownerId);
        Task<IReadOnlyList<CourseListItemDto>> GetListAsync(Guid currentUserId);
        Task<CourseDetailsDto?> GetDetailsAsync(Guid courseId, Guid currentUserId);
        /// <summary>Обновить курс (только владелец).</summary>
        Task UpdateAsync(Guid courseId, Guid currentUserId, UpdateCourseRequest request);

        /// <summary>Удалить курс (опционально, только владелец).</summary>
        Task DeleteAsync(Guid courseId, Guid currentUserId);
    }
}
