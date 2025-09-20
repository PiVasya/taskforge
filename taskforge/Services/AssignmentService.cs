using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class AssignmentService : IAssignmentService
    {
        private readonly ApplicationDbContext _db;

        public AssignmentService(ApplicationDbContext db) => _db = db;

        public async Task<Guid> CreateAsync(Guid courseId, CreateAssignmentRequest req, Guid authorId)
        {
            // Права: только владелец курса
            var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId);
            if (course == null) throw new KeyNotFoundException("Course not found");
            if (course.OwnerId != authorId) throw new UnauthorizedAccessException("Not course owner");

            var task = new TaskAssignment
            {
                Id = Guid.NewGuid(),
                CourseId = courseId,
                Title = req.Title,
                Description = req.Description,
                Type = string.IsNullOrWhiteSpace(req.Type) ? "code-test" : req.Type,
                Tags = req.Tags,
                Difficulty = req.Difficulty,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            foreach (var t in req.TestCases ?? Enumerable.Empty<CreateTestCaseRequest>())
            {
                task.TestCases.Add(new TaskTestCase
                {
                    Id = Guid.NewGuid(),
                    Input = t.Input,
                    ExpectedOutput = t.ExpectedOutput,
                    IsHidden = t.IsHidden,
                });
            }

            _db.TaskAssignments.Add(task);
            await _db.SaveChangesAsync();
            return task.Id;
        }

        public async Task<IReadOnlyList<AssignmentListItemDto>> GetByCourseAsync(Guid courseId, Guid currentUserId)
        {
            // доступ к курсу: public или владелец
            var course = await _db.Courses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == courseId);
            if (course == null) throw new KeyNotFoundException("Course not found");
            if (!(course.IsPublic || course.OwnerId == currentUserId)) throw new UnauthorizedAccessException();

            var list = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.CourseId == courseId)
                .OrderBy(a => a.CreatedAt)
                .Select(a => new AssignmentListItemDto
                {
                    Id = a.Id,
                    Title = a.Title,
                    Difficulty = a.Difficulty,
                    Tags = a.Tags,
                    CreatedAt = a.CreatedAt,
                    SolvedByCurrentUser = a.Solutions.Any(s => s.UserId == currentUserId && s.PassedAllTests)
                })
                .ToListAsync();

            return list;
        }

        public async Task<AssignmentDetailsDto?> GetDetailsAsync(Guid assignmentId, Guid currentUserId)
        {
            var a = await _db.TaskAssignments
                .Include(x => x.Course)
                .Include(x => x.TestCases)
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == assignmentId);

            if (a == null) return null;
            if (!(a.Course.IsPublic || a.Course.OwnerId == currentUserId)) return null;

            var dto = new AssignmentDetailsDto
            {
                Id = a.Id,
                CourseId = a.CourseId,
                Title = a.Title,
                Description = a.Description,
                Type = a.Type,
                Tags = a.Tags,
                Difficulty = a.Difficulty,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt,
                TotalTests = a.TestCases.Count,
                PublicTests = a.TestCases.Count(t => !t.IsHidden),
                SolvedByCurrentUser = _db.UserTaskSolutions
                    .Any(s => s.TaskAssignmentId == a.Id && s.UserId == currentUserId && s.PassedAllTests),
                PublicSamples = a.TestCases
                    .Where(t => !t.IsHidden)
                    .Take(3)
                    .Select(t => new PublicSampleDto { Input = t.Input })
                    .ToList()
            };
            return dto;
        }
    }
}
