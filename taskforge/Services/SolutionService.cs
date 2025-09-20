using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class SolutionService : ISolutionService
    {
        private readonly ApplicationDbContext _db;
        private readonly ICompilerService _compiler;

        public SolutionService(ApplicationDbContext db, ICompilerService compiler)
        {
            _db = db;
            _compiler = compiler;
        }

        public async Task<SubmitSolutionResponse> SubmitAsync(Guid assignmentId, Guid userId, SubmitSolutionRequest req)
        {
            var assignment = await _db.TaskAssignments
                .Include(a => a.Course)
                .Include(a => a.TestCases)
                .SingleOrDefaultAsync(a => a.Id == assignmentId);

            if (assignment == null) throw new KeyNotFoundException("Assignment not found");
            if (!(assignment.Course.IsPublic || assignment.Course.OwnerId == userId))
                throw new UnauthorizedAccessException();

            if (!string.Equals(assignment.Type, "code-test", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Only 'code-test' assignments are supported right now");

            // Готовим тесты для компилятора
            var testReq = new TestRunRequestDto
            {
                Language = req.Language,
                Code = req.Code,
                TestCases = assignment.TestCases.Select(t => new TestCaseDto
                {
                    Input = t.Input,
                    ExpectedOutput = t.ExpectedOutput
                }).ToList()
            };

            var results = await _compiler.RunTestsAsync(testReq);

            var passed = results.Count(r => r.Passed);
            var failed = results.Count - passed;

            // Сохраняем решение (последнее состояние)
            _db.UserTaskSolutions.Add(new UserTaskSolution
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TaskAssignmentId = assignmentId,
                SubmittedCode = req.Code,
                Language = req.Language,
                PassedAllTests = failed == 0 && results.Count > 0,
                PassedCount = passed,
                FailedCount = failed,
                SubmittedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            // Отдаём пользователю без ExpectedOutput и без фактов о hidden-тестах (только флаг)
            var response = new SubmitSolutionResponse
            {
                PassedAllTests = failed == 0 && results.Count > 0,
                PassedCount = passed,
                FailedCount = failed,
                Results = results.Zip(assignment.TestCases, (r, t) => new TestRunResultDto
                {
                    Input = t.IsHidden ? "(hidden)" : t.Input,
                    ActualOutput = r.Passed ? null : r.ActualOutput, // можно вернуть при фейле
                    Passed = r.Passed,
                    IsHidden = t.IsHidden
                }).ToList()
            };

            return response;
        }
    }
}
