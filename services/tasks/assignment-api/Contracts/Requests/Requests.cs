using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;


namespace TaskForge.Tasks.Api.Contracts;

public sealed record SolvedAssignmentsRequest(Guid[]? AssignmentIds);

public sealed record AssignmentIdsRequest(Guid[]? AssignmentIds);

public sealed record UserIdsRequest(Guid[] UserIds);

public sealed record CourseIdsRequest(Guid[]? CourseIds);

public sealed record ActivityLeaderboardRequest(Guid? CourseId, int? Days, Guid[]? UserIds, Guid[]? CourseIds = null);

public sealed record AssignmentRequest(Guid? Id, string? Title, string? Description, string? Type, string? Language, List<string>? AllowedLanguages, string? Tags, int? Difficulty, int? Rating, string? StarterCode, string? TestsJson, JsonElement? Tests, JsonElement? TestCases, List<string>? CodeForbiddenCalls, List<string>? CodeRequiredCalls, bool? IsVisible, bool? IsHidden, int? Sort, string? ImageTestReferenceKey, int? ImageTestSimilarityThreshold, JsonElement? AnalyticsSettings = null);

public sealed record ImageCodeRequest(string? Language, string? Code, string? Input, int? TimeoutSeconds);

public sealed record AnalyzerRequest(
    string Language,
    string Profile,
    string Source,
    [property: System.Text.Json.Serialization.JsonPropertyName("extra_forbidden")] object? ExtraForbidden,
    [property: System.Text.Json.Serialization.JsonPropertyName("forbidden_calls")] string[]? ForbiddenCalls,
    [property: System.Text.Json.Serialization.JsonPropertyName("required_calls")] string[]? RequiredCalls);

public sealed record InternalImageSolutionRequest(Guid UserId, Guid AssignmentId, string? Language, string? Code, int SimilarityPercent, bool Passed, JsonElement? Result);

public sealed record SortRequest(int Sort);

public sealed record PositionRequest(int? Position, Guid? AfterAssignmentId);

public sealed record VisibilityRequest(bool IsVisible);

public sealed record AssignmentActivityBatchRequest(string? SessionId, List<AssignmentActivityEventRequest>? Events);

public sealed record AssignmentActivityEventRequest(
    string? EventType,
    string? EventUid,
    int? Sequence,
    DateTimeOffset? ClientTime,
    JsonElement? Payload,
    int? CodeLength,
    int? CodeDelta,
    string? CodeHash,
    string? CodeSample,
    string? FullCode,
    int? TextLength,
    string? TextHash,
    string? TextSample,
    string? Language,
    Guid? AttemptId,
    Guid? SubmissionId,
    long? ActiveDurationMs,
    long? HiddenDurationMs,
    long? BlurDurationMs);
