// Services/JudgeService.cs
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using taskforge.Data;
using taskforge.Data.Models.DTO.Solutions;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    /// Оркестратор проверки: подгрузка тестов, запуск, парсинг ошибок.
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
            // Гарантируем наличие compile/run для стабильного рендера на фронте
            var result = new JudgeResponseDto
            {
                Status = "passed",
                Message = "OK",
                Compile = new JudgeResponseDto.CompileInfo
                {
                    Ok = true,
                    Stdout = string.Empty,
                    Stderr = string.Empty,
                    Diagnostics = new List<JudgeResponseDto.Diagnostic>()
                },
                Run = new JudgeResponseDto.RunInfo
                {
                    ExitCode = 0,
                    Stdout = string.Empty,
                    Stderr = string.Empty
                }
            };

            // 1) Загрузка задания + тестов
            var task = await _db.TaskAssignments
                .Include(t => t.TestCases)
                .FirstOrDefaultAsync(t => t.Id == req.AssignmentId);

            if (task == null)
                return new JudgeResponseDto
                {
                    Status = "infrastructure_error",
                    Message = "Задание не найдено",
                    Compile = result.Compile,
                    Run = result.Run
                };

            // 2) «Пробная» компиляция (через CompileAndRun с пустым stdin)
            object comp;
            try
            {
                comp = await _compiler.CompileAndRunAsync(new()
                {
                    Language = req.Language,
                    Code = req.Source,
                    Input = "" // просто проверяем сборку
                });
            }
            catch (Exception ex) when (ex is HttpRequestException || ex.InnerException is HttpRequestException)
            {
                return new JudgeResponseDto
                {
                    Status = "infrastructure_error",
                    Message = "Сервис компиляции недоступен",
                    Compile = new JudgeResponseDto.CompileInfo
                    {
                        Ok = false,
                        Stdout = string.Empty,
                        Stderr = ex.Message,
                        Diagnostics = new()
                    },
                    Run = result.Run
                };
            }

            // Универсально читаем ответы от раннеров
            bool compOk = DynGetBool(comp, "Ok", "ok", "Success", "success");
            string? compOut = DynGetString(comp, "Stdout", "stdout", "Output", "output");
            string? compErr = DynGetString(comp, "Stderr", "stderr", "Error", "error");
            string? version = DynGetString(comp, "Version", "version");

            result.Version = version;
            result.Compile = new JudgeResponseDto.CompileInfo
            {
                Ok = compOk,
                Stdout = compOut ?? string.Empty,
                Stderr = compErr ?? string.Empty,
                Diagnostics = ParseDiagnostics(req.Language, compErr ?? string.Empty)
            };

            if (!compOk)
            {
                result.Status = "compile_error";
                result.Message = "Ошибка компиляции";
                return result;
            }

            // 3) Прогон по тестам
            var swTotal = Stopwatch.StartNew();

            foreach (var tc in task.TestCases)
            {
                object run;
                try
                {
                    run = await _compiler.CompileAndRunAsync(new()
                    {
                        Language = req.Language,
                        Code = req.Source,
                        Input = tc.Input ?? string.Empty,
                        TimeLimitMs = req.TimeLimitMs ?? 2000,
                        MemoryLimitMb = req.MemoryLimitMb ?? 256
                    });
                }
                catch (Exception ex) when (ex is HttpRequestException || ex.InnerException is HttpRequestException)
                {
                    result.Status = "infrastructure_error";
                    result.Message = "Сервис запуска недоступен";
                    result.Run = new JudgeResponseDto.RunInfo { ExitCode = -1, Stdout = "", Stderr = ex.Message };
                    return result;
                }

                int exitCode = DynGetInt(run, "ExitCode", "exitCode");
                string rStdout = DynGetString(run, "Stdout", "stdout", "Output", "output") ?? string.Empty;
                string? rStderr = DynGetString(run, "Stderr", "stderr", "Error", "error");
                int timeMs = DynGetInt(run, "TimeMs", "timeMs", "ElapsedMs", "elapsedMs");

                // === ВАЖНО: нормализуем текст для честного сравнения (чинит Python \n vs \r\n) ===
                var expected = Canon(tc.ExpectedOutput);
                var actual = Canon(rStdout);

                var testPassed = exitCode == 0 &&
                                 string.Equals(expected, actual, StringComparison.Ordinal);

                result.Tests.Add(new JudgeResponseDto.TestCaseResult
                {
                    Name = $"Тест {result.Tests.Count + 1}",
                    Input = tc.Input ?? "",
                    Expected = tc.ExpectedOutput ?? "",
                    Actual = rStdout,
                    Passed = testPassed,
                    Stdout = rStdout,
                    Stderr = rStderr,
                    TimeMs = timeMs,
                    Hidden = tc.IsHidden
                });

                if (exitCode != 0)
                {
                    result.Status = "runtime_error";
                    result.Message = "Исключение во время выполнения";
                    result.Run = new JudgeResponseDto.RunInfo
                    {
                        ExitCode = exitCode,
                        Stdout = rStdout,
                        Stderr = rStderr ?? ""
                    };
                    break;
                }
            }

            swTotal.Stop();
            result.Metrics = new JudgeResponseDto.ExecStats { TotalTimeMs = (int)swTotal.ElapsedMilliseconds };

            if (result.Status == "runtime_error") return result;

            bool hasFail = result.Tests.Exists(t => !t.Passed);
            result.Status = hasFail ? "failed_tests" : "passed";
            result.Message = hasFail ? "Есть непройденные тесты" : "Все тесты пройдены";
            return result;
        }

        // ===== Helpers =====

        /// <summary>
        /// Приводим вывод к канону: CRLF → LF, режем хвостовые пробелы у строк и конечные \n.
        /// Так «Hi\r\n» и «Hi\n» считаются равными. Это и чинит Python.
        /// </summary>
        private static string Canon(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            // 1) CRLF/CR → LF
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");

            // 2) убираем хвостовые пробелы/табы у каждой строки
            s = string.Join("\n", s.Split('\n').Select(line => line.TrimEnd(' ', '\t')));

            // 3) убираем конечные \n (несколько подряд тоже)
            s = s.TrimEnd('\n');

            return s;
        }

        private static string? DynGetString(object? obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (p != null) { var v = p.GetValue(obj); return v?.ToString(); }
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (f != null) { var v = f.GetValue(obj); return v?.ToString(); }
            }
            return null;
        }

        private static bool DynGetBool(object? obj, params string[] names)
        {
            var s = DynGetString(obj, names);
            if (s == null) return false;
            if (bool.TryParse(s, out var b)) return b;
            if (int.TryParse(s, out var i)) return i != 0;
            return false;
        }

        private static int DynGetInt(object? obj, params string[] names)
        {
            var s = DynGetString(obj, names);
            if (s == null) return 0;
            if (int.TryParse(s, out var i)) return i;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return (int)Math.Round(d);
            return 0;
        }

        private static List<JudgeResponseDto.Diagnostic> ParseDiagnostics(string lang, string stderr)
        {
            var list = new List<JudgeResponseDto.Diagnostic>();
            if (string.IsNullOrWhiteSpace(stderr)) return list;

            if (lang.Equals("csharp", StringComparison.OrdinalIgnoreCase))
            {
                var rx = new Regex(@"^(?<file>.*)\((?<line>\d+),(?<col>\d+)\):\s(?<level>error|warning)\s(?<code>[A-Za-z]+\d+):\s(?<msg>.*)$",
                    RegexOptions.Compiled | RegexOptions.Multiline);
                foreach (Match m in rx.Matches(stderr))
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
                var rx = new Regex(@"^(?<file>.*?):(?<line>\d+):(?<col>\d+):\s(?<level>fatal error|error|warning):\s(?<msg>.*)$",
                    RegexOptions.Compiled | RegexOptions.Multiline);
                foreach (Match m in rx.Matches(stderr))
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
                list.Add(new JudgeResponseDto.Diagnostic { Level = "error", Message = stderr.Trim() });
            }
            return list;
        }

        private static int? ToInt(string s) => int.TryParse(s, out var i) ? i : (int?)null;

        private static string ImproveMessage(string lang, string? code, string msg)
        {
            if (lang == "csharp" && code == "CS1002") return "Отсутствует «;». " + msg;
            if (lang == "cpp" && msg.Contains("expected ';'")) return "Отсутствует «;». " + msg;
            return msg;
        }
    }
}
