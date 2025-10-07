using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            var result = new JudgeResponseDto { Status = "passed", Message = "OK" };

            // 1) Загрузка задания + тестов
            var task = await _db.TaskAssignments
                .Include(t => t.TestCases)
                .FirstOrDefaultAsync(t => t.Id == req.AssignmentId);

            if (task == null)
                return new JudgeResponseDto { Status = "infrastructure_error", Message = "Задание не найдено" };

            // 2) «Пробная» компиляция (через CompileAndRun с пустым stdin)
            object comp;
            try
            {
                // NB: этот метод есть в текущем ICompilerService
                comp = await _compiler.CompileAndRunAsync(new()
                {
                    Language = req.Language,
                    Code     = req.Source,
                    Input    = ""     // ничего не подаём — нам важно, собралось ли
                });
            }
            catch (Exception ex) when (ex is HttpRequestException || ex.InnerException is HttpRequestException)
            {
                return new JudgeResponseDto
                {
                    Status = "infrastructure_error",
                    Message = "Сервис компиляции недоступен",
                    Compile = new JudgeResponseDto.CompileInfo { Ok = false, Stderr = ex.Message }
                };
            }

            // Универсально читаем ответы от раннеров
            bool   compOk    = DynGetBool(comp, "Ok", "ok", "Success", "success");
            string?compOut   = DynGetString(comp, "Stdout", "stdout", "Output", "output");
            string?compErr   = DynGetString(comp, "Stderr", "stderr", "Error", "error");
            string?version   = DynGetString(comp, "Version", "version");

            result.Version = version;
            result.Compile = new JudgeResponseDto.CompileInfo
            {
                Ok           = compOk,
                Stdout       = compOut,
                Stderr       = compErr,
                Diagnostics  = ParseDiagnostics(req.Language, compErr ?? string.Empty)
            };

            if (!compOk)
            {
                result.Status  = "compile_error";
                result.Message = "Ошибка компиляции";
                return result;
            }

            // 3) Прогон по тестам (через существующий сервис — он быстрый)
            var swTotal = Stopwatch.StartNew();

            foreach (var tc in task.TestCases)
            {
                object run;
                try
                {
                    // для совместимости используем тот же CompileAndRun, чтобы не плодить разные пути
                    run = await _compiler.CompileAndRunAsync(new()
                    {
                        Language      = req.Language,
                        Code          = req.Source,
                        Input         = tc.Input ?? string.Empty,
                        TimeLimitMs   = req.TimeLimitMs ?? 2000,
                        MemoryLimitMb = req.MemoryLimitMb ?? 256
                    });
                }
                catch (Exception ex) when (ex is HttpRequestException || ex.InnerException is HttpRequestException)
                {
                    result.Status = "infrastructure_error";
                    result.Message = "Сервис запуска недоступен";
                    result.Run = new JudgeResponseDto.RunInfo { ExitCode = -1, Stderr = ex.Message };
                    return result;
                }

                int     exitCode = DynGetInt(run, "ExitCode", "exitCode");
                string? rStdout  = DynGetString(run, "Stdout", "stdout", "Output", "output") ?? string.Empty;
                string? rStderr  = DynGetString(run, "Stderr", "stderr", "Error", "error");
                int     timeMs   = DynGetInt(run, "TimeMs", "timeMs", "ElapsedMs", "elapsedMs");

                var testPassed = string.Equals(
                    (rStdout ?? "").Trim(),
                    (tc.ExpectedOutput ?? "").Trim(),
                    StringComparison.Ordinal);

                result.Tests.Add(new JudgeResponseDto.TestCaseResult
                {
                    Name    = $"Тест {result.Tests.Count + 1}",
                    Input   = tc.Input ?? "",
                    Expected= tc.ExpectedOutput ?? "",
                    Actual  = rStdout ?? "",
                    Passed  = exitCode == 0 && testPassed,
                    Stdout  = rStdout,
                    Stderr  = rStderr,
                    TimeMs  = timeMs
                });

                if (exitCode != 0)
                {
                    result.Status  = "runtime_error";
                    result.Message = "Исключение во время выполнения";
                    result.Run = new JudgeResponseDto.RunInfo
                    {
                        ExitCode = exitCode, Stdout = rStdout, Stderr = rStderr
                    };
                    break;
                }
            }

            swTotal.Stop();
            result.Metrics = new JudgeResponseDto.ExecStats { TotalTimeMs = (int)swTotal.ElapsedMilliseconds };

            if (result.Status == "runtime_error") return result;

            bool hasFail = result.Tests.Exists(t => !t.Passed);
            result.Status  = hasFail ? "failed_tests" : "passed";
            result.Message = hasFail ? "Есть непройденные тесты" : "Все тесты пройдены";
            return result;
        }

        // ===== Helpers =====

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
                        Level  = m.Groups["level"].Value,
                        Message= ImproveMessage(lang, m.Groups["code"].Value, m.Groups["msg"].Value),
                        Code   = m.Groups["code"].Value,
                        File   = m.Groups["file"].Value,
                        Line   = ToInt(m.Groups["line"].Value),
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
                        Level  = m.Groups["level"].Value.StartsWith("fatal") ? "error" : m.Groups["level"].Value,
                        Message= ImproveMessage(lang, null, m.Groups["msg"].Value),
                        Code   = null,
                        File   = m.Groups["file"].Value,
                        Line   = ToInt(m.Groups["line"].Value),
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

        private static int? ToInt(string s) => int.TryParse(s, out var i) ? i : null;

        private static string ImproveMessage(string lang, string? code, string msg)
        {
            if (lang == "csharp" && code == "CS1002") return "Отсутствует «;». " + msg;
            if (lang == "cpp"    && msg.Contains("expected ';'")) return "Отсутствует «;». " + msg;
            return msg;
        }
    }
}
