using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;


namespace TaskForge.Solutions.Api.Contracts;

public sealed record AssignmentMetadata(Guid AssignmentId, Guid CourseId, string Title, string CourseTitle, int Rating);

public sealed record AssignmentAccessDto(Guid AssignmentId, Guid CourseId, Guid UserId, bool CanView, bool CanSubmit, bool IsVisible, bool CanEdit);

public sealed record CourseAccessDto(Guid CourseId, Guid UserId, bool CanView, bool CanEdit, bool IsPublic);

public sealed class AssignmentSummaryDto
{
    public Guid Id { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid CourseId { get; set; }
    public string? Title { get; set; }
    public string? AssignmentTitle { get; set; }
    public int Rating { get; set; } = 1;
}

public sealed class CourseSummaryDto
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public string? Title { get; set; }
    public string? CourseTitle { get; set; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total, bool HasMore);

public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? MaskedEmail { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Location { get; set; }
    public string? Education { get; set; }
    public bool ShowInLeaderboard { get; set; } = true;
    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Login;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = MaskedEmail;
    }
}

public sealed class GroupMembersResponse
{
    public Guid GroupId { get; set; }
    public Guid[]? UserIds { get; set; }
}

public sealed class TaskActivityRowDto
{
    public Guid UserId { get; set; }
    public Guid AssignmentId { get; set; }
    public int Rating { get; set; } = 1;
    public DateTimeOffset SubmittedAt { get; set; }
    public string? Kind { get; set; }
}

public sealed record SolvedAssignmentsResponse(Guid UserId, Guid[] SolvedAssignmentIds);

public sealed record EnqueueResult(bool Created, Guid? JobId, string? Message, JsonElement? Raw);

public sealed record JudgeRunResult(string Verdict, int Score, bool PassedAllTests, bool CountInRating, string Message, JsonElement? Results, JsonElement? Raw, bool CompileError);

public sealed record QuotaView(string bucket, int remaining, int capacity, int retryAfterSeconds, DateTimeOffset nextRefillAtUtc, bool allowed);
