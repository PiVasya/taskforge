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

public sealed record CourseAccessDto(Guid CourseId, Guid UserId, bool CanView, bool CanEdit, bool IsPublic);

public sealed record SolvedAssignmentsResponse(Guid UserId, Guid[]? SolvedAssignmentIds);

public sealed record AssignmentAccessDto(Guid AssignmentId, Guid CourseId, Guid UserId, bool CanView, bool CanSubmit, bool IsVisible, bool CanEdit);

public sealed record AssignmentSummaryDto(Guid Id, Guid AssignmentId, Guid CourseId, string Title, string AssignmentTitle, string Type, string Language, int Rating, int Difficulty, bool IsVisible, int Sort);

public sealed class CourseSummaryDto
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public string? Title { get; set; }
    public string? CourseTitle { get; set; }
}

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
    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Login;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = MaskedEmail;
    }
}

public sealed record UploadedFileDto(string Key, string? PrivateUrl, string ContentType, string FileName, long Size);

public sealed record ImageCaseResult(int Index, string Name, string Input, string? ExpectedOutput, string? ActualOutput, bool StdoutPassed, bool ImagePassed, bool Passed, double Similarity, double SimilarityPercent, int Threshold, int ThresholdPercent, bool IsHidden, string? ReferenceUrl, string? SubmittedUrl, string? Stderr, JsonElement? Analyzer);

public sealed record TestSettings(int MaxAttempts, int PassPercent, bool ShuffleQuestions, bool ShuffleAnswers, bool AllowReview, List<int?> AttemptTimeLimitsSeconds);

public sealed record MathSettings(int MaxAttempts, int PassPercent, bool ShuffleBlocks, bool AllowReview, List<int?> AttemptTimeLimitsSeconds);
