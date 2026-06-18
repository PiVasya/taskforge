using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TaskForge.Execution.Worker;

public sealed partial class Worker
{
    private sealed record ClaimNextResponse(ExecutionJobDto? Job);
    private sealed record ExecutionJobDto(Guid Id, Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, string? TestsJson, string? CodeForbiddenCallsJson, string? CodeRequiredCallsJson, int? TimeLimitMs, int? MemoryLimitMb, int AttemptCount, string Status);
    private sealed record AnalyzerRequest(string Language, string Source, [property: JsonPropertyName("extra_forbidden")] object? ExtraForbidden, [property: JsonPropertyName("forbidden_calls")] string[]? ForbiddenCalls, [property: JsonPropertyName("required_calls")] string[]? RequiredCalls);
    private sealed record RunnerTestsRequest(string Code, JsonElement[] Tests, int? TimeLimitMs, int? MemoryLimitMb);
    private sealed record CompleteExecutionJobRequest(string Status, string? Stdout, string? Stderr, int? ExitCode, long DurationMs, int Score, bool Passed, JsonElement? Result);
    private sealed record SolutionVerdictRequest(string Verdict, int Score, string Message, JsonElement? Result);

    private sealed record RunnerResult(string Verdict, int Score, bool Passed, JsonElement? Raw, JsonElement? Results, string? Stdout, string? Stderr, int? ExitCode, bool CompileError)
    {
        public string Message => Verdict switch
        {
            "Accepted" => "Все тесты пройдены.",
            "CompileError" => "Ошибка компиляции.",
            "PolicyFailed" => "Код содержит запрещённые конструкции.",
            "NoTestsConfigured" => "Для задания не настроены тесты.",
            "JudgeUnavailable" => "Judge pipeline временно недоступен.",
            _ => "Не все тесты пройдены."
        };

        public JsonElement? ToSolutionJson()
        {
            return CloneJson(JsonSerializer.Serialize(new
            {
                verdict = Verdict,
                score = Score,
                passedAllTests = Passed,
                compileError = CompileError,
                policyFailed = string.Equals(Verdict, "PolicyFailed", StringComparison.OrdinalIgnoreCase),
                message = Message,
                results = Results,
                cases = Results,
                raw = Raw
            }, JsonOptions));
        }

        public static RunnerResult NoTests() => new("NoTestsConfigured", 0, false, null, null, null, null, null, false);
        public static RunnerResult PolicyFailed(JsonElement raw, string message) => new("PolicyFailed", 0, false, raw, null, null, message, null, false);
        public static RunnerResult Error(string verdict, string message, JsonElement? raw = null) => new(verdict, 0, false, raw, null, null, message, null, false);
    }
}
