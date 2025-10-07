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
    /// <summary>
    /// Оркестратор: загрузить тесты, вызвать компилятор/раннер, распарсить stderr, собрать отчёт.
    /// Работает поверх существующего ICompilerService (контейнеры-раннеры).
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

            // 2) Компиляция (или подготовка к запуску)
            dynamic comp;
            try
            {
                comp = await Call(_compiler, "CompileAsync", req.Language, req.Source);
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
                        Stderr = ex.Message
                    }
                };
            }

            bool compOk = DynGetBool(comp, "Ok", "ok");
            string? compStdErr = DynGetString(comp, "Stderr", "stderr");
            string? compStdOut = DynGetString(comp, "Stdout", "stdout");
            string? artifactId = DynGetString(comp, "ArtifactId", "artifactId", "artifact");
            string? version = DynGetString(comp, "Version", "version");

            var compileInfo = new JudgeResponseDto.CompileInfo
            {
                Ok = compOk,
                Stdout = compStdOut,
                Stderr = compStdErr,
                Diagnostics = ParseDiagnostics(req.Language, compStdErr ?? string.Empty)
            };
            result.Compile = compileInfo;
            result.Version = version;

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
                dynamic run;
                try
                {
                    run = await Call(_compiler, "RunAsync",
                        req.Language,
                        artifactId,                              // из компиляции
                        tc.Input ?? string.Empty,                // stdin
                        req.TimeLimitMs ?? 2000,                 // дефолт 2с
                        req.MemoryLimitMb ?? 256                 // дефолт 256 МБ
                    );
                }
                catch (Exception ex) when (ex is HttpRequestException || ex.InnerException is HttpRequestException)
                {
                    result.Status = "infrastructure_error";
                    result.Message = "Сервис запуска недоступен";
                    result.Run = new JudgeResponseDto.RunInfo
                    {
                        ExitCode = -1,
                        Stderr = ex.Message
                    };
                    return result;
                }

                int exitCode = DynGetInt(run, "ExitCode", "exitCode");
                string? rStdout = DynGetString(run, "Stdout", "stdout") ?? string.Empty;
                string? rStderr = DynGetString(run, "Stderr", "stderr");
                int timeMs = DynGetInt(run, "TimeMs", "timeMs", "ElapsedMs", "elapsedMs");

                // если раннер прислал Exception-объект — разберём его
                var exObj = DynGetObject(run, "Exception", "exception");
                JudgeResponseDto.ExceptionInfo? exInfo = null;
                if (exObj != null)
                {
                    exInfo = new JudgeResponseDto.ExceptionInfo
                    {
                        Type = DynGetString(exObj, "Type", "type") ?? "",
                        Message = DynGetString(exObj, "Message", "message") ?? "",
                        StackTrace = DynGetString(exObj, "StackTrace", "stackTrace"),
                        Line = DynGetNullableInt(exObj, "Line", "line"),
                        Column = DynGetNullableInt(exObj, "Column", "column")
                    };
                }
                else if (!string.IsNullOrWhiteSpace(rStderr) && req.Language == "python")
                {
                    // python traceback → попытаемся вытащить тип/сообщение/строку
                    exInfo = ParsePythonTraceback(rStderr);
                }

                var testPassed = string.Equals(
                    (rStdout ?? "").Trim(),
                    (tc.ExpectedOutput ?? "").Trim(),
                    StringComparison.Ordinal);

                result.Tests.Add(new JudgeResponseDto.TestCaseResult
                {
                    Name = $"Тест {result.Tests.Count + 1}",
                    Input = tc.Input ?? "",
                    Expected = tc.ExpectedOutput ?? "",
                    Actual = rStdout ?? "",
                    Passed = exitCode == 0 && exInfo == null && testPassed,
                    Stdout = rStdout,
                    Stderr = rStderr,
                    TimeMs = timeMs
                });

                if (exitCode != 0 || exInfo != null)
                {
                    result.Status = "runtime_error";
                    result.Message = "Исключение во время выполнения";
                    result.Run = new JudgeResponseDto.RunInfo
                    {
                        ExitCode = exitCode,
                        Stdout = rStdout,
                        Stderr = rStderr,
                        Exception = exInfo
                    };
                    break;
                }
            }
            swTotal.Stop();
            result.Metrics = new JudgeResponseDto.ExecStats { TotalTimeMs = (int)swTotal.ElapsedMilliseconds };

            if (result.Status == "runtime_error") return result;

            var hasFail = result.Tests.Exists(t => !t.Passed);
            result.Status = hasFail ? "failed_tests" : "passed";
            result.Message = hasFail ? "Есть непройденные тесты" : "Все тесты пройдены";
            return result;
        }

        // === helpers: динамический вызов и чтение полей (компиляторы — в отдельных контейнерах и могут возвращать
        // чуть разные имена полей; читаем безопасно) ===

        private static async Task<object?> Call(object target, string method, params object?[] args)
        {
            var mi = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (mi == null)
                throw new MissingMethodException($"Method {method} not found on {target.GetType().Name}");
            var task = (Task)mi.Invoke(target, args)!;
            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result");
            return resultProp?.GetValue(task);
        }

        private static string? DynGetString(object? obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (p != null)
                {
                    var v = p.GetValue(obj);
                    return v?.ToString();
                }
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

        private static int? DynGetNullableInt(object? obj, params string[] names)
        {
            var s = DynGetString(obj, names);
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (int.TryParse(s, out var i)) return i;
            return null;
        }
        // вернуть объект свойства/поля по одному из имён (без приведения к строке)
        private static object? DynGetObject(object? obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (p != null) return p.GetValue(obj);

                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (f != null) return f.GetValue(obj);
            }
            return null;
        }

        // === Парсеры диагностик ===

        private static List<JudgeResponseDto.Diagnostic> ParseDiagnostics(string lang, string stderr)
        {
            var list = new List<JudgeResponseDto.Diagnostic>();
            if (string.IsNullOrWhiteSpace(stderr)) return list;

            if (lang.Equals("csharp", StringComparison.OrdinalIgnoreCase))
            {
                // Path\File.cs(12,24): error CS1002: ; expected
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
                // main.cpp:17:5: error: expected ';' after expression
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
                // У компиляции питона чаще всего нет диагностик, но вернём stderr целиком как один элемент
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
            if (lang == "csharp" && code == "CS1002")
            {
                // типичная ошибка — ";" expected
                return "Отсутствует «;». " + msg;
            }
            if (lang == "cpp" && msg.Contains("expected ';'"))
            {
                return "Отсутствует «;». " + msg;
            }
            return msg;
        }

        private static JudgeResponseDto.ExceptionInfo? ParsePythonTraceback(string stderr)
        {
            // Ищем последнюю пару: File "...", line N и строку "Type: Message"
            var rxFile = new Regex(@"File ""(?<file>.+?)"", line (?<line>\d+)", RegexOptions.Multiline);
            var rxLast = new Regex(@"^(?<type>[A-Za-z_]\w*):\s(?<msg>.+)$", RegexOptions.Multiline);

            int? line = null;
            var mFile = rxFile.Matches(stderr);
            if (mFile.Count > 0)
            {
                var last = mFile[mFile.Count - 1];
                line = ToInt(last.Groups["line"].Value);
            }

            string type = "Exception";
            string message = stderr.Trim();
            var mLast = rxLast.Matches(stderr);
            if (mLast.Count > 0)
            {
                var last = mLast[mLast.Count - 1];
                type = last.Groups["type"].Value;
                message = last.Groups["msg"].Value;
            }

            return new JudgeResponseDto.ExceptionInfo
            {
                Type = type,
                Message = message,
                Line = line
            };
        }
    }
}
