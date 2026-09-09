using System.Text.Json.Serialization;

namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>Private expected output for one immutable spec/engine target.</summary>
public sealed class SqlExpectedArtifact : ISqlMutableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EngineTargetId { get; set; }
    public string ArtifactKey { get; set; } = string.Empty;
    public int FormatVersion { get; set; } = 1;
    public string Status { get; set; } = SqlValidationStatuses.Pending;
    public Guid ValidationRunId { get; set; } = Guid.NewGuid();
    public Guid? ExecutionJobId { get; set; }
    // Small artifacts may be inline; larger artifacts use a private content-addressed key.
    [JsonIgnore] public string? ExpectedJson { get; set; }
    [JsonIgnore] public string? ObjectKey { get; set; }
    [JsonIgnore] public string? ContentHash { get; set; }
    public long? ByteLength { get; set; }
    [JsonIgnore] public string? ErrorCode { get; set; }
    [JsonIgnore] public string? DiagnosticJson { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
