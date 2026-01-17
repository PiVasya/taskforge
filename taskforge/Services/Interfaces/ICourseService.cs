﻿using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICourseService
    {
        Task<Guid> CreateAsync(CreateCourseRequest req, Guid currentUserId, string? role);
        Task<IReadOnlyList<CourseListItemDto>> GetListAsync(Guid currentUserId, string? role);
        Task<CourseDetailsDto?> GetDetailsAsync(Guid courseId, Guid currentUserId, string? role);
        Task UpdateAsync(Guid courseId, Guid currentUserId, string? role, UpdateCourseRequest request);
        Task DeleteAsync(Guid courseId, Guid currentUserId, string? role);
    }
}
