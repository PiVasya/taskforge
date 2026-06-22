using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;


namespace TaskForge.Solutions.Api.Contracts;

public sealed record AssignmentIdsRequest(Guid[]? AssignmentIds);

public sealed record CourseIdsRequest(Guid[]? CourseIds);

public sealed record UserIdsRequest(Guid[]? UserIds);

public sealed record RatingDirtyUsersRequest(Guid[]? UserIds, string? Reason, Guid? AssignmentId);

public sealed record ActivityLeaderboardRequest(Guid? CourseId, int? Days, Guid[]? UserIds, Guid[]? CourseIds = null);

public sealed record SolvedAssignmentsRequest(Guid[]? AssignmentIds);

public sealed record SubmitRequest(string? Language, string? Code, string? Input, JsonElement? Tests);

public sealed record SolutionVerdictRequest(string? Verdict, int Score, string? Message, JsonElement? Result);

public sealed record InternalImageSolutionRequest(Guid UserId, Guid AssignmentId, string? Language, string? Code, int SimilarityPercent, bool Passed, JsonElement? Result);

public sealed record CreateExecutionJobRequest(Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, JsonElement[]? Tests, int? TimeLimitMs, int? MemoryLimitMb, string? TestsJson, string[]? CodeForbiddenCalls, string[]? CodeRequiredCalls);

public sealed record BadgeUserRequest(Guid UserId, Guid BadgeId);
