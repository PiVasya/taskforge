using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;
using taskforge.Data.Models.Entities;

namespace taskforge.Services
{
    /// <summary>
    /// Сервис администрирования решений пользователей.
    /// </summary>
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
                .AsQueryable();

            if (assignmentId.HasValue) q = q.Where(s => s.TaskAssignmentId == assignmentId.Value);
            if (courseId.HasValue)     q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);

            // сортировка по времени — новые сверху
            q = q.OrderByDescending(s => s.SubmittedAt);

            return await q.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200))
                .Select(s => new SolutionListItemDto
                {
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

            return s == null ? null : MapToDetailsDto(s);
        }

        public async Task<IList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid? courseId, int? days, int top)
        {
            var since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : (DateTime?)null;

            var q = _db.UserTaskSolutions
                .AsNoTracking()
                .Where(s => s.PassedAllTests);

            if (since.HasValue)
                q = q.Where(s => s.SubmittedAt >= since.Value);

            if (courseId.HasValue)
                q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);

            var data = await q
                .GroupBy(s => new { s.UserId, s.TaskAssignmentId })
                .Select(g => new { g.Key.UserId, g.Key.TaskAssignmentId })
                .GroupBy(x => x.UserId)
                .Select(g => new { UserId = g.Key, Solved = g.Count() })
                .OrderByDescending(x => x.Solved)
                .Take(Math.Clamp(top, 1, 100))
                .ToListAsync();

            var userIds = data.Select(d => d.UserId).ToArray();
            var users = await _db.Users
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id);

            return data.Select(d =>
            {
                var u = users[d.UserId];
                return new LeaderboardEntryDto
                {
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
            query = (query ?? string.Empty).Trim();
            var q = _db.Users.AsNoTracking();

            if (!string.IsNullOrEmpty(query))
            {
                q = q.Where(u =>
                    u.Email.Contains(query) ||
                    u.FirstName.Contains(query) ||
                    u.LastName.Contains(query));
            }

            return await q.OrderBy(u => u.Email)
                .Take(Math.Clamp(take, 1, 100))
                .Select(u => new UserShortDto
                {
                    Id = u.Id,
                    Email = u.Email,
                    FirstName = u.FirstName,
                    LastName = u.LastName
                })
                .ToListAsync();
        }

        public async Task<IList<SolutionListItemDto>> GetAllByUserAsync(Guid userId, Guid? courseId, Guid? assignmentId, int? days)
        {
            var q = _db.UserTaskSolutions
                .AsNoTracking()
                .Where(s => s.UserId == userId)
                .Include(s => s.TaskAssignment)
                    .ThenInclude(a => a.Course)
                .AsQueryable();

            if (assignmentId.HasValue) q = q.Where(s => s.TaskAssignmentId == assignmentId.Value);
            if (courseId.HasValue)     q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);
            if (days.HasValue)
            {
                var since = DateTime.UtcNow.AddDays(-days.Value);
                q = q.Where(s => s.SubmittedAt >= since);
            }

            return await q.OrderByDescending(s => s.SubmittedAt)
                .Select(s => new SolutionListItemDto
                {
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

        public async Task DeleteUserSolutionsAsync(Guid userId, Guid? courseId, Guid? assignmentId)
        {
            var q = _db.UserTaskSolutions.Where(s => s.UserId == userId);
            if (assignmentId.HasValue) q = q.Where(s => s.TaskAssignmentId == assignmentId.Value);
            if (courseId.HasValue)     q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);

            var list = await q.ToListAsync();
            if (list.Count == 0) return;

            _db.UserTaskSolutions.RemoveRange(list);
            await _db.SaveChangesAsync();
        }

        public async Task<List<SolutionDetailsDto>> GetDetailsBulkAsync(IEnumerable<Guid> ids)
        {
            var idSet = (ids ?? Array.Empty<Guid>()).ToHashSet();
            if (idSet.Count == 0) return new List<SolutionDetailsDto>();

            var entities = await _db.UserTaskSolutions
                .AsNoTracking()
                .Where(s => idSet.Contains(s.Id))
                .Include(s => s.TaskAssignment)
                    .ThenInclude(a => a.Course)
                .ToListAsync();

            return entities.Select(MapToDetailsDto).ToList();
        }

        // ---- приватный маппер для единообразия ----
        private static SolutionDetailsDto MapToDetailsDto(UserTaskSolution s)
        {
            return new SolutionDetailsDto
            {
                Id = s.Id,
                UserId = s.UserId,
                TaskAssignmentId = s.TaskAssignmentId,
                Language = s.Language ?? string.Empty,
                SubmittedCode = s.SubmittedCode ?? string.Empty,
                PassedAllTests = s.PassedAllTests,
                PassedCount = s.PassedCount,
                FailedCount = s.FailedCount,
                SubmittedAt = s.SubmittedAt,
                CourseTitle = s.TaskAssignment?.Course?.Title ?? string.Empty,
                AssignmentTitle = s.TaskAssignment?.Title ?? string.Empty,
                Cases = null
            };
        }
    }
}
