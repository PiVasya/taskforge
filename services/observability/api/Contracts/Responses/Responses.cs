using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;


namespace TaskForge.Observability.Api.Contracts;

public sealed class ActivityUserDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Role { get; set; }
}

public sealed class ActivityItemDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Description { get; set; }
    public int? StatusCode { get; set; }
    public bool IsAuthenticated { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public ActivityUserDto? User { get; set; }
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
    public string? Role { get; set; }
    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Login;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = MaskedEmail;
    }
}
