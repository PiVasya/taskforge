﻿using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICourseService
    {
        Task<Guid> CreateAsync(CreateCourseRequest req, Guid ownerId);
        Task<IReadOnlyList<CourseListItemDto>> GetListAsync(Guid currentUserId, bool isAdmin);
        Task<CourseDetailsDto?> GetDetailsAsync(Guid courseId, Guid currentUserId, bool isAdmin);
        Task UpdateAsync(Guid courseId, Guid currentUserId, bool isAdmin, UpdateCourseRequest request);
        Task DeleteAsync(Guid courseId, Guid currentUserId, bool isAdmin);
    }
}
