using System.Text.Json.Serialization;

namespace TaskForge.Tasks.Api.Domain.Sql;

public sealed class SqlAssignmentSpecVersion : ISqlImmutableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public int Version { get; set; }
    public Guid DatasetVersionId { get; set; }
    public int ContractVersion { get; set; } = 1;
    public string Mode { get; set; } = SqlTaskModes.Result;
    public string? StarterSql { get; set; }
    [JsonIgnore] public string? ReferenceSql { get; set; }
    public bool AllowMultipleStatements { get; set; }
    [JsonIgnore] public string ResultComparisonSettingsJson { get; set; } = "{}";
    [JsonIgnore] public string StateCheckSettingsJson { get; set; } = "{}";
    [JsonIgnore] public string SchemaCheckSettingsJson { get; set; } = "{}";
    public string LimitsJson { get; set; } = "{}";
    public string VerifierVersion { get; set; } = "1";
    [JsonIgnore] public string SpecHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
