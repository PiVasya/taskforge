using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;
public sealed class AssignmentService : IAssignmentService
{
    private readonly ApplicationDbContext _db;

    public AssignmentService(ApplicationDbContext db) => _db = db;

    public async Task<Guid> CreateAsync(Guid courseId, CreateAssignmentRequest req, Guid currentUserId)
    {
        // (опционально) проверить, что текущий юзер владелец курса
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) throw new InvalidOperationException("Курс не найден");
        // if (course.OwnerId != currentUserId) throw new UnauthorizedAccessException();

        var entity = new TaskAssignment
        {
            Id = Guid.NewGuid(),
            CourseId = courseId,
            Title = req.Title,
            Description = req.Description,
            Difficulty = req.Difficulty,
            Tags = req.Tags,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        foreach (var tc in req.TestCases)
        {
            entity.TestCases.Add(new TaskTestCase
            {
                Id = Guid.NewGuid(),
                Input = tc.Input,
                ExpectedOutput = tc.ExpectedOutput,
                IsHidden = tc.IsHidden
            });
        }

        _db.TaskAssignments.Add(entity);
        await _db.SaveChangesAsync();

        return entity.Id;
    }

    public async Task<IList<AssignmentListItemDto>> GetByCourseAsync(Guid courseId, Guid currentUserId)
    {
        return await _db.TaskAssignments
            .Where(a => a.CourseId == courseId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AssignmentListItemDto
            {
                Id = a.Id,
                Title = a.Title,
                Description = a.Description, // 👈 добавлено
                Difficulty = a.Difficulty,
                Tags = a.Tags,
                CreatedAt = a.CreatedAt,
                SolvedByCurrentUser = a.Solutions.Any(s => s.UserId == currentUserId && s.PassedAllTests)
            })
            .ToListAsync();
    }

    public async Task<AssignmentDetailsDto?> GetDetailsAsync(Guid assignmentId, Guid currentUserId)
    {
        var a = await _db.TaskAssignments
            .Include(x => x.TestCases)
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
            CreatedAt = a.CreatedAt,
            PublicTestCount = a.TestCases.Count(x => !x.IsHidden),
            HiddenTestCount = a.TestCases.Count(x => x.IsHidden),
            SolvedByCurrentUser = a.Solutions.Any(s => s.PassedAllTests)
        };
    }

    public async Task UpdateAsync(Guid assignmentId, Guid currentUserId, UpdateAssignmentRequest request)
    {
        var task = await _db.Set<TaskAssignment>()
            .Include(a => a.Course)
            .Include(a => a.TestCases)
            .FirstOrDefaultAsync(a => a.Id == assignmentId);

        if (task == null)
            throw new KeyNotFoundException("Assignment not found");

        if (task.Course.OwnerId != currentUserId)
            throw new UnauthorizedAccessException("Only course owner can edit this assignment.");

        task.Title = request.Title.Trim();
        task.Description = request.Description; // markdown/html — не режем
        task.Type = request.Type.Trim();
        task.Tags = request.Tags?.Trim();
        task.Difficulty = request.Difficulty;
        task.UpdatedAt = DateTime.UtcNow;

        // replace-all testcases
        _db.Set<TaskTestCase>().RemoveRange(task.TestCases);
        task.TestCases.Clear();

        foreach (var tc in request.TestCases)
        {
            task.TestCases.Add(new TaskTestCase
            {
                Id = Guid.NewGuid(),
                TaskAssignmentId = task.Id,
                Input = tc.Input,
                ExpectedOutput = tc.ExpectedOutput,
                IsHidden = tc.IsHidden
            });
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
}
