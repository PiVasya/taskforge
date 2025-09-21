using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class CourseService : ICourseService
    {
        private readonly ApplicationDbContext _db;

        public CourseService(ApplicationDbContext db) => _db = db;

        public async Task<Guid> CreateAsync(CreateCourseRequest req, Guid ownerId)
        {
            var course = new Course
            {
                Id = Guid.NewGuid(),
                Title = req.Title,
                Description = req.Description,
                IsPublic = req.IsPublic,
                OwnerId = ownerId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Courses.Add(course);
            await _db.SaveChangesAsync();
            return course.Id;
        }

        public async Task<IReadOnlyList<CourseListItemDto>> GetListAsync(Guid currentUserId)
        {
            // видны public + свои
            var query = _db.Courses
                .AsNoTracking()
                .Select(c => new CourseListItemDto
                {
                    Id = c.Id,
                    Title = c.Title,
                    Description = c.Description,
                    IsPublic = c.IsPublic,
                    OwnerId = c.OwnerId,
                    CreatedAt = c.CreatedAt,
                    AssignmentCount = c.Assignments.Count,
                    SolvedCountForCurrentUser = c.Assignments.Count(a =>
                        a.Solutions.Any(s => s.UserId == currentUserId && s.PassedAllTests))
                })
                .Where(x => x.IsPublic || x.OwnerId == currentUserId)
                .OrderByDescending(x => x.CreatedAt);

            return await query.ToListAsync();
        }

        public async Task<CourseDetailsDto?> GetDetailsAsync(Guid courseId, Guid currentUserId)
        {
            var dto = await _db.Courses.AsNoTracking()
                .Where(c => c.Id == courseId)
                .Select(c => new CourseDetailsDto
                {
                    Id = c.Id,
                    Title = c.Title,
                    Description = c.Description,
                    IsPublic = c.IsPublic,
                    OwnerId = c.OwnerId,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    AssignmentCount = c.Assignments.Count,
                    SolvedCountForCurrentUser = c.Assignments.Count(a =>
                        a.Solutions.Any(s => s.UserId == currentUserId && s.PassedAllTests))
                })
                .SingleOrDefaultAsync();

            // доступ: публичный или владелец
            if (dto != null && !(dto.IsPublic || dto.OwnerId == currentUserId))
                return null;

            return dto;
        }

        public async Task UpdateAsync(Guid courseId, Guid currentUserId, UpdateCourseRequest request)
        {
            var course = await _db.Set<Course>().FirstOrDefaultAsync(c => c.Id == courseId)
                         ?? throw new KeyNotFoundException("Course not found");

            if (course.OwnerId != currentUserId)
                throw new UnauthorizedAccessException("Only owner can edit the course.");

            course.Title = request.Title.Trim();
            course.Description = request.Description?.Trim();
            course.IsPublic = request.IsPublic;
            course.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid courseId, Guid currentUserId)
        {
            var course = await _db.Set<Course>()
                                  .Include(c => c.Assignments)
                                  .ThenInclude(a => a.TestCases)
                                  .FirstOrDefaultAsync(c => c.Id == courseId)
                         ?? throw new KeyNotFoundException("Course not found");

            if (course.OwnerId != currentUserId)
                throw new UnauthorizedAccessException("Only owner can delete the course.");

            _db.Remove(course);
            await _db.SaveChangesAsync();
        }
    }
}
