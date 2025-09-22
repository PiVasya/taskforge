using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
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
        private readonly IHttpContextAccessor _http;
        private bool CanRevealHidden()
        {
            // _http или контекст может быть null (тесты, фоновые вызовы, некорректная регистрация) — в таком случае просто не раскрываем.
            var user = _http?.HttpContext?.User;
            if (user == null || !(user.Identity?.IsAuthenticated ?? false))
                return false;

            // Основная проверка ролей
            if (user.IsInRole("Admin") || user.IsInRole("Teacher") || user.IsInRole("Editor"))
                return true;

            // На случай, если роли приходят не в ClaimTypes.Role, а в "role"/"roles"
            var roles = user.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
                .Select(c => c.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return roles.Contains("Admin") || roles.Contains("Teacher") || roles.Contains("Editor");
        }

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

            // Детерминированный порядок тестов: по Id
            var orderedCases = a.TestCases
                .OrderBy(tc => tc.Id)
                .ToList();

            // Прогоняем ВСЕ тесты
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

            // Кому можно показывать содержимое скрытых тестов
            var canReveal = CanRevealHidden(); // ваш хелпер из прошлого сообщения

            // Собираем DTO ответа
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

            // Сохранение решения
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