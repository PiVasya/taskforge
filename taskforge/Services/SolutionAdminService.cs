using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services;

public sealed class SolutionAdminService : ISolutionAdminService
{
    private readonly ApplicationDbContext _db;
    public SolutionAdminService(ApplicationDbContext db) => _db = db;

    public async Task<IList<SolutionListItemDto>> GetByUserAsync(Guid userId, Guid? courseId, Guid? assignmentId, int skip, int take)
    {
        var q = _db.UserTaskSolutions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .Include(s => s.TaskAssignment)
            .ThenInclude(a => a.Course)
            .OrderByDescending(s => s.SubmittedAt)
            .AsQueryable();

        if (assignmentId.HasValue) q = q.Where(s => s.TaskAssignmentId == assignmentId.Value);
        if (courseId.HasValue) q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);

        return await q.Skip(skip).Take(take)
            .Select(s => new SolutionListItemDto {
                Id = s.Id,
                SubmittedAt = s.SubmittedAt,
                CourseTitle = s.TaskAssignment.Course.Title,
                AssignmentTitle = s.TaskAssignment.Title,
                Language = s.Language,
                PassedAllTests = s.PassedAllTests,
                PassedCount = s.PassedCount,
                FailedCount = s.FailedCount
            })
            .ToListAsync();
    }

    public async Task<SolutionDetailsDto?> GetDetailsAsync(Guid solutionId)
    {
        var s = await _db.UserTaskSolutions
            .AsNoTracking()
            .Include(x => x.TaskAssignment)
            .ThenInclude(a => a.Course)
            .FirstOrDefaultAsync(x => x.Id == solutionId);

        if (s == null) return null;

        return new SolutionDetailsDto {
            Id = s.Id,
            UserId = s.UserId,
            TaskAssignmentId = s.TaskAssignmentId,
            Language = s.Language,
            SubmittedCode = s.SubmittedCode,
            PassedAllTests = s.PassedAllTests,
            PassedCount = s.PassedCount,
            FailedCount = s.FailedCount,
            SubmittedAt = s.SubmittedAt,
            CourseTitle = s.TaskAssignment.Course.Title,
            AssignmentTitle = s.TaskAssignment.Title,
            // Cases = ...  // если будете хранить/подтягивать
        };
    }

    public async Task<IList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid? courseId, int? days, int top)
    {
        var since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : (DateTime?)null;

        var q = _db.UserTaskSolutions.AsNoTracking().Where(s => s.PassedAllTests);
        if (since.HasValue) q = q.Where(s => s.SubmittedAt >= since.Value);
        if (courseId.HasValue)
            q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);

        var grouped = q
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Solved = g.Count() })
            .OrderByDescending(x => x.Solved)
            .Take(top);

        var data = await grouped.ToListAsync();
        var users = await _db.Users
            .Where(u => data.Select(d => d.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        return data.Select(d => {
            var u = users[d.UserId];
            return new LeaderboardEntryDto {
                UserId = u.Id,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                Solved = d.Solved
            };
        }).ToList();
    }

    public async Task<IList<UserShortDto>> SearchUsersAsync(string query, int take)
    {
        query = (query ?? "").Trim();
        var q = _db.Users.AsNoTracking();

        if (!string.IsNullOrEmpty(query))
        {
            q = q.Where(u =>
                u.Email.Contains(query) ||
                u.FirstName.Contains(query) ||
                u.LastName.Contains(query));
        }

        return await q.OrderBy(u => u.Email).Take(take)
            .Select(u => new UserShortDto {
                Id = u.Id, Email = u.Email, FirstName = u.FirstName, LastName = u.LastName
            })
            .ToListAsync();
    }
}
