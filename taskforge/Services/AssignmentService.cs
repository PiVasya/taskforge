using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class AssignmentService : IAssignmentService
    {
        private readonly ApplicationDbContext _db;

        public AssignmentService(ApplicationDbContext db) => _db = db;

        public async Task<Guid> CreateAsync(Guid courseId, CreateAssignmentRequest req, Guid currentUserId)
        {
            // (опционально) проверить право владения курсом
            var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId)
                         ?? throw new InvalidOperationException("Курс не найден");
            // if (course.OwnerId != currentUserId) throw new UnauthorizedAccessException();
            var maxSort = await _db.TaskAssignments
                .Where(a => a.CourseId == courseId)
                .Select(a => (int?)a.Sort)
                .MaxAsync() ?? -1;
                
            var entity = new TaskAssignment
            {
                Id = Guid.NewGuid(),
                CourseId = courseId,
                Title = (req.Title ?? string.Empty).Trim(),
                Description = req.Description,
                Difficulty = req.Difficulty,
                Tags = req.Tags,
                Type = (req.Type ?? "code-test").Trim(),
                Sort = maxSort + 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // тесты из запроса
            if (req.TestCases != null)
            {
                foreach (var tc in req.TestCases)
                {
                    entity.TestCases.Add(new TaskTestCase
                    {
                        Id = Guid.NewGuid(),
                        Input = tc.Input ?? string.Empty,
                        ExpectedOutput = tc.ExpectedOutput ?? string.Empty,
                        IsHidden = tc.IsHidden
                    });
                }
            }

            _db.TaskAssignments.Add(entity);
            await _db.SaveChangesAsync();
            return entity.Id;
        }

        public async Task<IList<AssignmentListItemDto>> GetByCourseAsync(Guid courseId, Guid currentUserId)
        {
            return await _db.TaskAssignments
                .Where(a => a.CourseId == courseId)
                .OrderBy(a => a.Sort)
                .ThenByDescending(a => a.CreatedAt)
                .Select(a => new AssignmentListItemDto
                {
                    Id = a.Id,
                    Title = a.Title,
                    Description = a.Description,
                    Difficulty = a.Difficulty,
                    Tags = a.Tags,
                    CreatedAt = a.CreatedAt,
                    SolvedByCurrentUser = a.Solutions.Any(s => s.UserId == currentUserId && s.PassedAllTests),
                    Sort = a.Sort
                })
                .ToListAsync();
        }

        public async Task<AssignmentDetailsDto?> GetDetailsAsync(Guid assignmentId, Guid currentUserId)
        {
            var a = await _db.TaskAssignments
                .AsNoTracking()
                .Include(x => x.TestCases) // важно: подтягиваем тесты
                .Include(x => x.Solutions.Where(s => s.UserId == currentUserId))
                .FirstOrDefaultAsync(x => x.Id == assignmentId);

            if (a == null) return null;

            return new AssignmentDetailsDto
            {
                Id = a.Id,
                CourseId = a.CourseId,
                Title = a.Title,
                Description = a.Description,
                Difficulty = a.Difficulty,
                Tags = a.Tags,
                Type = a.Type,
                CreatedAt = a.CreatedAt,
                PublicTestCount = a.TestCases.Count(x => !x.IsHidden),
                HiddenTestCount = a.TestCases.Count(x => x.IsHidden),
                SolvedByCurrentUser = a.Solutions.Any(s => s.PassedAllTests),

                // ---- ВАЖНО: отдать тесты на фронт для формы редактирования ----
                TestCases = a.TestCases
                    .OrderBy(tc => tc.Id) // если нет поля Order, сортируем стабильно по Id
                    .Select(tc => new AssignmentTestCaseDto
                    {
                        Id = tc.Id,
                        Input = tc.Input,
                        ExpectedOutput = tc.ExpectedOutput,
                        IsHidden = tc.IsHidden
                    })
                    .ToList(),
                Sort = a.Sort
            };
        }

        public async Task UpdateAsync(Guid assignmentId, Guid currentUserId, UpdateAssignmentRequest request)
        {
            var task = await _db.Set<TaskAssignment>()
                .Include(a => a.Course)
                .FirstOrDefaultAsync(a => a.Id == assignmentId);

            if (task == null)
                throw new KeyNotFoundException("Assignment not found");

            if (task.Course.OwnerId != currentUserId)
                throw new UnauthorizedAccessException("Only course owner can edit this assignment.");

            // --- поля задания ---
            task.Title = (request.Title ?? string.Empty).Trim();
            task.Description = request.Description;
            task.Type = (request.Type ?? "code-test").Trim();
            task.Tags = request.Tags?.Trim();
            task.Difficulty = request.Difficulty;
            task.UpdatedAt = DateTime.UtcNow;

            // --- ПОЛНАЯ ЗАМЕНА ТЕСТОВ ---
            // Снести старые тесты одной командой SQL (быстрее и без конфликтов отслеживания)
            await _db.Set<TaskTestCase>()
                .Where(tc => tc.TaskAssignmentId == task.Id)
                .ExecuteDeleteAsync();

            // Добавить новые
            if (request.TestCases != null && request.TestCases.Count > 0)
            {
                var newCases = request.TestCases.Select(tc => new TaskTestCase
                {
                    Id = Guid.NewGuid(),
                    TaskAssignmentId = task.Id,
                    Input = tc.Input ?? string.Empty,
                    ExpectedOutput = tc.ExpectedOutput ?? string.Empty,
                    IsHidden = tc.IsHidden
                });

                await _db.Set<TaskTestCase>().AddRangeAsync(newCases);
            }

            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid assignmentId, Guid currentUserId)
        {
            var task = await _db.Set<TaskAssignment>()
                .Include(a => a.Course)
                .Include(a => a.TestCases)
                .FirstOrDefaultAsync(a => a.Id == assignmentId)
                ?? throw new KeyNotFoundException("Assignment not found");

            if (task.Course.OwnerId != currentUserId)
                throw new UnauthorizedAccessException("Only course owner can delete assignment.");

            _db.Remove(task);
            await _db.SaveChangesAsync();
        }

        public async Task UpdateSortAsync(Guid assignmentId, Guid currentUserId, int sort)
        {
            var task = await _db.Set<TaskAssignment>()
                .Include(a => a.Course)
                .FirstOrDefaultAsync(a => a.Id == assignmentId)
                ?? throw new KeyNotFoundException("Assignment not found");

            if (task.Course.OwnerId != currentUserId)
                throw new UnauthorizedAccessException("Only course owner can reorder assignment.");

            task.Sort = sort;
            task.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
