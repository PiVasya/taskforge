using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;


namespace TaskForge.Execution.Api.Contracts;

public sealed record RunnerRequest(string? Language, string? Code, string? Input, JsonElement[]? TestCases, JsonElement[]? Tests, int? TimeLimitMs, int? MemoryLimitMb);

public sealed record CreateInteractiveCompilerSessionRequest(string? Language, string? Code, int? Columns, int? Rows, int? TimeLimitMs, int? MemoryLimitMb);

public sealed record CreateExecutionJobRequest(Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, JsonElement? TestCases, JsonElement? Tests, string? TestsJson, int? TimeLimitMs, int? MemoryLimitMb, JsonElement? CodeForbiddenCalls, JsonElement? CodeRequiredCalls, List<string>? PolicyForbiddenCalls, List<string>? PolicyRequiredCalls);

public sealed record CompleteExecutionJobRequest(string? Status, string? Stdout, string? Stderr, int? ExitCode, long DurationMs, int Score, bool Passed, JsonElement? Result);
