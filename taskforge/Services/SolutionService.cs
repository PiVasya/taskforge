// Services/SolutionService.cs
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
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

        public async Task<SubmitSolutionResultDto> SubmitAsync(Guid assignmentId, Guid currentUserId, SubmitSolutionRequest req)
        {
            Console.WriteLine($"[Solution] Submit: assignmentId={assignmentId} user={currentUserId} lang={req.Language} code.len={req.Code?.Length ?? 0}");

            var a = await _db.TaskAssignments
                .Include(x => x.TestCases)
                .FirstOrDefaultAsync(x => x.Id == assignmentId);

            if (a == null) throw new InvalidOperationException("Задание не найдено");

            var orderedCases = a.TestCases
                .OrderBy(tc => tc.Id)
                .ToList();

            Console.WriteLine($"[Solution] testCases={orderedCases.Count}");

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
            Console.WriteLine($"[Solution] results={results.Count}; passed={results.Count(x=>x.Passed)} failed={results.Count(x=>!x.Passed)}");

            var canReveal = CanRevealHidden();
            Console.WriteLine($"[Solution] canRevealHidden={canReveal}");

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

            Console.WriteLine($"[Solution] summary: passed={full.Passed} failed={full.Failed} all={full.PassedAll}");

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
    }
}
