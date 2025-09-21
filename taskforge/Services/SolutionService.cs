using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
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

        public async Task<SubmitSolutionResultDto> SubmitAsync(Guid assignmentId, Guid currentUserId, SubmitSolutionRequest req)
        {
            var a = await _db.TaskAssignments
                .Include(x => x.TestCases)
                .FirstOrDefaultAsync(x => x.Id == assignmentId);

            if (a == null) throw new InvalidOperationException("Задание не найдено");

            // прогоняем ВСЕ тесты (и публичные, и скрытые)
            var testReq = new TestRunRequestDto
            {
                Language = req.Language,
                Code = req.Code,
                TestCases = a.TestCases.Select(tc => new TestCaseDto
                {
                    Input = tc.Input,
                    ExpectedOutput = tc.ExpectedOutput
                }).ToList()
            };

            var results = await _compiler.RunTestsAsync(testReq);

            // собираем DTO для ответа (но скрытые тесты «маскируем» на фронте по флагу)
            var full = new SubmitSolutionResultDto();
            for (int i = 0; i < a.TestCases.Count; i++)
            {
                var tc = a.TestCases.ElementAt(i);
                var r = results.ElementAt(i);

                full.Cases.Add(new SolutionCaseResultDto
                {
                    Input = tc.Input,
                    Expected = tc.ExpectedOutput,
                    Actual = r.ActualOutput,
                    Passed = r.Passed,
                    Hidden = tc.IsHidden
                });
            }

            full.Passed = full.Cases.Count(c => c.Passed);
            full.Failed = full.Cases.Count(c => !c.Passed);
            full.PassedAll = full.Failed == 0;

            // записываем решение
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