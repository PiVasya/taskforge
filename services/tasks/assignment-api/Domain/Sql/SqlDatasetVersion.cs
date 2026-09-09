namespace TaskForge.Tasks.Api.Domain.Sql;

public sealed class SqlDatasetVersion : ISqlImmutableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DatasetId { get; set; }
    public int Version { get; set; }
    public int DefinitionSchemaVersion { get; set; } = 1;
    public string DefinitionJson { get; set; } = "{}";
    public string SeedJson { get; set; } = "{}";
    // Type/default overrides belong to the dataset, not to a referencing assignment.
    public string EngineOverridesJson { get; set; } = "{}";
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
