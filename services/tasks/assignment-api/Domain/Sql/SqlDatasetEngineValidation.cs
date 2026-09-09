using System.Text.Json.Serialization;

namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>A durable compatibility receipt, not proof of a local GOLDEN/READY cache.</summary>
public sealed class SqlDatasetEngineValidation : ISqlMutableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DatasetVersionId { get; set; }
    public Guid EngineProfileId { get; set; }
    public string MaterializationKey { get; set; } = string.Empty;
    public string Status { get; set; } = SqlValidationStatuses.Pending;
    // A new run id fences callbacks from a previous validation run.
    public Guid ValidationRunId { get; set; } = Guid.NewGuid();
    public Guid? ExecutionJobId { get; set; }
    [JsonIgnore] public string? ErrorCode { get; set; }
    [JsonIgnore] public string? DiagnosticJson { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
