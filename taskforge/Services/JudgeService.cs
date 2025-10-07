using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.DTO.Solutions;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    /// <summary>
    /// Оркестратор запуска решений: подгружает тесты задания, дергает общий ICompilerService
    /// (который прокидывает запросы в HTTP-раннеры), собирает нормализованный отчёт.
    /// Никакой рефлексии и скрытых методов — только CompileAndRunAsync / RunTestsAsync.
    /// </summary>
    public sealed class JudgeService : IJudgeService
    {
        private readonly ApplicationDbContext _db;
        private readonly ICompilerService _compiler;

        public JudgeService(ApplicationDbContext db, ICompilerService compiler)
        {
            _db = db;
            _compiler = compiler;
        }

        public async Task<JudgeResponseDto> JudgeAsync(JudgeRequestDto req, Guid currentUserId)
        {
            var result = new JudgeResponseDto { Status = "passed", Message = "OK" };

            // 1) Загрузка задания + тесты
            var task = await _db.TaskAssignments
                .Include(t => t.TestCases)
                .FirstOrDefaultAsync(t => t.Id == req.AssignmentId);

            if (task == null)
            {
                result.Status = "infrastructure_error";
                result.Message = "Задание не найдено";
                return result;
            }

            // 2) Предварительная компиляция/пробный запуск для выявления ошибок компиляции
            CompilerRunResponseDto probe;
            try
            {
                probe = await _compiler.CompileAndRunAsync(new CompilerRunRequestDto
                {
                    Language = req.Language,
                    Code = req.Source,
                    Input = string.Empty
                });
            }
            catch (Exception ex)
            {
                result.Status = "infrastructure_error";
                result.Message = $"Сервис компиляции недоступен: {ex.Message}";
                result.Compile = new JudgeResponseDto.CompileInfo
                {
                    Ok = false,
                    Stderr = ex.Message
                };
                return result;
            }

            var stderr = probe.Error ?? string.Empty;
            var diagnostics = ParseDiagnostics(req.Language, stderr);

            result.Compile = new JudgeResponseDto.CompileInfo
            {
                Ok = string.IsNullOrEmpty(stderr),
                Stdout = probe.Output,
                Stderr = stderr,
                Diagnostics = diagnostics
            };

            if (!string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(probe.Output))
            {
                // Компилятор упал/ошибка компиляции
                result.Status = "compile_error";
                result.Message = "Ошибка компиляции";
                return result;
            }

            // 3) Прогон по тестам через раннер
            var swTotal = Stopwatch.StartNew();
            List<TestCaseDto> tests = new();
            foreach (var tc in task.TestCases)
            {
                tests.Add(new TestCaseDto { Input = tc.Input, ExpectedOutput = tc.ExpectedOutput });
            }

            IList<TestResultDto> runResults;
            try
            {
                runResults = await _compiler.RunTestsAsync(new TestRunRequestDto
                {
                    Language = req.Language,
                    Code = req.Source,
                    TestCases = tests
                });
            }
            catch (Exception ex)
            {
                result.Status = "infrastructure_error";
                result.Message = $"Сервис запуска недоступен: {ex.Message}";
                result.Run = new JudgeResponseDto.RunInfo
                {
                    ExitCode = -1,
                    Stderr = ex.Message
                };
                return result;
            }

            // 4) Сбор отчёта
            for (int i = 0; i < tests.Count; i++)
            {
                var t = tests[i];
                var rr = i < runResults.Count ? runResults[i] : new TestResultDto();

                result.Tests.Add(new JudgeResponseDto.TestCaseResult
                {
                    Name = $"Тест {i + 1}",
                    Input = t.Input ?? string.Empty,
                    Expected = t.ExpectedOutput ?? string.Empty,
                    Actual = rr.ActualOutput ?? string.Empty,
                    Passed = rr.Passed,
                    Stdout = rr.ActualOutput, // в наших раннерах stdout == actualOutput
                    Stderr = null,
                    TimeMs = null
                });
            }

            swTotal.Stop();
            result.Metrics = new JudgeResponseDto.ExecStats { TotalTimeMs = (int)swTotal.ElapsedMilliseconds };

            // 5) Итоговый статус
            var firstFailIndex = result.Tests.FindIndex(x => !x.Passed);
            if (firstFailIndex >= 0)
            {
                // Попробуем получить stderr для «не прошёл» — одиночным запуском, чтобы красиво показать ошибку выполнения.
                try
                {
                    var single = await _compiler.CompileAndRunAsync(new CompilerRunRequestDto
                    {
                        Language = req.Language,
                        Code = req.Source,
                        Input = result.Tests[firstFailIndex].Input
                    });

                    if (!string.IsNullOrEmpty(single.Error) && string.IsNullOrEmpty(single.Output))
                    {
                        result.Status = "runtime_error";
                        result.Message = "Исключение во время выполнения";
                        result.Run = new JudgeResponseDto.RunInfo
                        {
                            ExitCode = -1,
                            Stdout = single.Output,
                            Stderr = single.Error
                        };
                        return result;
                    }
                }
                catch { /* не фейлим — останемся в failed_tests */ }

                result.Status = "failed_tests";
                result.Message = "Есть непройденные тесты";
                return result;
            }

            result.Status = "passed";
            result.Message = "Все тесты пройдены";
            return result;
        }

        // === Парсеры диагностик компиляции (stderr → список Diagnostic) ===
        private static List<JudgeResponseDto.Diagnostic> ParseDiagnostics(string lang, string stderr)
        {
            var list = new List<JudgeResponseDto.Diagnostic>();
            if (string.IsNullOrWhiteSpace(stderr)) return list;

            if (lang.Equals("csharp", StringComparison.OrdinalIgnoreCase))
            {
                // Path\File.cs(12,24): error CS1002: ; expected
                var rx = new System.Text.RegularExpressions.Regex(@"^(?<file>.*)\((?<line>\d+),(?<col>\d+)\):\s(?<level>error|warning)\s(?<code>[A-Za-z]+\d+):\s(?<msg>.*)$",
                    System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(stderr))
                {
                    list.Add(new JudgeResponseDto.Diagnostic
                    {
                        Level = m.Groups["level"].Value,
                        Message = ImproveMessage(lang, m.Groups["code"].Value, m.Groups["msg"].Value),
                        Code = m.Groups["code"].Value,
                        File = m.Groups["file"].Value,
                        Line = ToInt(m.Groups["line"].Value),
                        Column = ToInt(m.Groups["col"].Value)
                    });
                }
            }
            else if (lang.Equals("cpp", StringComparison.OrdinalIgnoreCase))
            {
                // main.cpp:17:5: error: expected ';' after expression
                var rx = new System.Text.RegularExpressions.Regex(@"^(?<file>.*?):(?<line>\d+):(?<col>\d+):\s(?<level>fatal error|error|warning):\s(?<msg>.*)$",
                    System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(stderr))
                {
                    list.Add(new JudgeResponseDto.Diagnostic
                    {
                        Level = m.Groups["level"].Value.StartsWith("fatal") ? "error" : m.Groups["level"].Value,
                        Message = ImproveMessage(lang, null, m.Groups["msg"].Value),
                        Code = null,
                        File = m.Groups["file"].Value,
                        Line = ToInt(m.Groups["line"].Value),
                        Column = ToInt(m.Groups["col"].Value)
                    });
                }
            }
            else if (lang.Equals("python", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new JudgeResponseDto.Diagnostic
                {
                    Level = "error",
                    Message = stderr.Trim()
                });
            }
            return list;
        }

        private static int? ToInt(string s) => int.TryParse(s, out var i) ? i : null;

        private static string ImproveMessage(string lang, string? code, string msg)
        {
            if (lang == "csharp" && code == "CS1002") return "Отсутствует «;». " + msg;
            if (lang == "cpp" && msg.Contains("expected ';'")) return "Отсутствует «;». " + msg;
            return msg;
        }
    }
}
