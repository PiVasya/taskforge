using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using taskforge.Data;
using taskforge.Data.Models;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
// We use DTOs from Data.Models.DTO rather than a separate Dtos namespace.
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    /// <summary>
    /// Service responsible for processing user task solutions: running code against test cases,
    /// saving results and retrieving aggregated statistics such as leaderboards.
    /// </summary>
    public sealed class SolutionService : ISolutionService
    {
        private readonly ApplicationDbContext _db;
        private readonly ICompilerService _compiler;
        private readonly IHttpContextAccessor _http;

        private bool CanRevealHidden()
        {
            var user = _http?.HttpContext?.User;
            if (user == null || !(user.Identity?.IsAuthenticated ?? false))
                return false;

            if (user.IsInRole("Admin") || user.IsInRole("Teacher") || user.IsInRole("Editor"))
                return true;

            var roles = user.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
                .Select(c => c.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return roles.Contains("Admin") || roles.Contains("Teacher") || roles.Contains("Editor");
        }

        public SolutionService(ApplicationDbContext db, ICompilerService compiler, IHttpContextAccessor http)
        {
            _db = db;
            _compiler = compiler;
            _http = http;
        }

        /// <inheritdoc />
        public async Task<SubmitSolutionResultDto> SubmitAsync(Guid assignmentId, Guid currentUserId, SubmitSolutionRequest req)
        {
            var a = await _db.TaskAssignments
                .Include(x => x.TestCases)
                .FirstOrDefaultAsync(x => x.Id == assignmentId);

            if (a == null) throw new InvalidOperationException("Задание не найдено");

            var orderedCases = a.TestCases
                .OrderBy(tc => tc.Id)
                .ToList();

            var testReq = new TestRunRequestDto
            {
                Language = req.Language,
                Code = req.Code,
                TestCases = orderedCases.Select(tc => new TestCaseDto
                {
                    Input = tc.Input,
                    ExpectedOutput = tc.ExpectedOutput
                }).ToList()
            };

            var results = await _compiler.RunTestsAsync(testReq);

            var canReveal = CanRevealHidden();

            var full = new SubmitSolutionResultDto();

            for (int i = 0; i < orderedCases.Count; i++)
            {
                var tc = orderedCases[i];
                var r = results[i];

                var isHiddenForUser = tc.IsHidden && !canReveal;

                full.Cases.Add(new SolutionCaseResultDto
                {
                    Input = isHiddenForUser ? null : tc.Input,
                    Expected = isHiddenForUser ? null : tc.ExpectedOutput,
                    Actual = isHiddenForUser ? null : r.ActualOutput,
                    Passed = r.Passed,
                    Hidden = tc.IsHidden
                });
            }

            full.Passed = full.Cases.Count(c => c.Passed);
            full.Failed = full.Cases.Count(c => !c.Passed);
            full.PassedAll = full.Failed == 0;

            var solution = new UserTaskSolution
            {
                Id = Guid.NewGuid(),
                UserId = currentUserId,
                TaskAssignmentId = assignmentId,
                SubmittedCode = req.Code,
                Language = req.Language,
                PassedAllTests = full.PassedAll,
                PassedCount = full.Passed,
                FailedCount = full.Failed,
                SubmittedAt = DateTime.UtcNow
            };

            _db.UserTaskSolutions.Add(solution);
            await _db.SaveChangesAsync();

            return full;
        }

                public async Task<List<TopSolutionDto>> GetTopSolutionsAsync(Guid assignmentId, int count)
        {
            var query = _db.UserTaskSolutions
                .Include(s => s.User)
                .Where(s => s.TaskAssignmentId == assignmentId)
                .OrderByDescending(s => s.PassedCount)
                .ThenBy(s => s.SubmittedAt)
                .Take(count);

            var list = await query
                .Select(s => new TopSolutionDto
                {
                    // Map Guid identifiers directly
                    UserId = s.UserId,
                    UserName = ((s.User.FirstName ?? string.Empty) + " " + (s.User.LastName ?? string.Empty)).Trim(),
                    PassedCount = s.PassedCount,
                    FailedCount = s.FailedCount,
                    SubmittedAt = s.SubmittedAt,
                    Language = s.Language,
                    Code = s.SubmittedCode
                })
                .ToListAsync();

            return list;
        }

        /// <summary>
        /// Список решений текущего пользователя с пагинацией.
        /// </summary>
        public async Task<IList<SolutionListItemDto>> GetMySolutionsAsync(
            Guid currentUserId,
            Guid? courseId,
            Guid? assignmentId,
            int skip,
            int take)
        {
            var q = _db.UserTaskSolutions
                .AsNoTracking()
                .Where(s => s.UserId == currentUserId)
                .Include(s => s.TaskAssignment)
                    .ThenInclude(a => a.Course)
                .AsQueryable();

            if (assignmentId.HasValue)
                q = q.Where(s => s.TaskAssignmentId == assignmentId.Value);

            if (courseId.HasValue)
                q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);

            q = q.OrderByDescending(s => s.SubmittedAt);

            return await q
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 200))
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

        /// <summary>
        /// Все решения текущего пользователя (или за период), без пагинации.
        /// </summary>
        public async Task<IList<SolutionListItemDto>> GetMySolutionsAllAsync(
            Guid currentUserId,
            Guid? courseId,
            Guid? assignmentId,
            int? days)
        {
            var q = _db.UserTaskSolutions
                .AsNoTracking()
                .Where(s => s.UserId == currentUserId)
                .Include(s => s.TaskAssignment)
                    .ThenInclude(a => a.Course)
                .AsQueryable();

            if (assignmentId.HasValue)
                q = q.Where(s => s.TaskAssignmentId == assignmentId.Value);

            if (courseId.HasValue)
                q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);

            if (days.HasValue)
            {
                var since = DateTime.UtcNow.AddDays(-days.Value);
                q = q.Where(s => s.SubmittedAt >= since);
            }

            q = q.OrderByDescending(s => s.SubmittedAt);

            return await q
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

        /// <summary>
        /// Детали одного решения текущего пользователя (код решения).
        /// </summary>
        public async Task<SolutionDetailsDto?> GetMySolutionDetailsAsync(Guid currentUserId, Guid solutionId)
        {
            var s = await _db.UserTaskSolutions
                .AsNoTracking()
                .Include(x => x.TaskAssignment)
                    .ThenInclude(a => a.Course)
                .FirstOrDefaultAsync(x => x.Id == solutionId && x.UserId == currentUserId);

            if (s == null)
                return null;

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
