using System.Text.Json.Serialization;

namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>Part of an immutable spec revision; changing targets creates a new revision.</summary>
public sealed class SqlAssignmentEngineTarget : ISqlImmutableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SpecVersionId { get; set; }
    public Guid EngineProfileId { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public string? StarterSqlOverride { get; set; }
    [JsonIgnore] public string? ReferenceSqlOverride { get; set; }
    // null means inherit; a supplied JSON object replaces the whole corresponding setting.
    [JsonIgnore] public string? ResultComparisonSettingsOverrideJson { get; set; }
    [JsonIgnore] public string? StateCheckSettingsOverrideJson { get; set; }
    [JsonIgnore] public string? SchemaCheckSettingsOverrideJson { get; set; }
}
