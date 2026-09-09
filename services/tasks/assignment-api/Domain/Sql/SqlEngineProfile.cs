namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>An exact runtime contract, not an engine alias or a worker health record.</summary>
public sealed class SqlEngineProfile : ISqlImmutableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
    public string DisplayName { get; set; } = string.Empty;
    public string Engine { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    // Image digest for server engines; binary/build digest for embedded engines.
    public string RuntimeDigest { get; set; } = string.Empty;
    public string AdapterVersion { get; set; } = string.Empty;
    public int SettingsSchemaVersion { get; set; } = 1;
    // Encoding, collation, timezone, SQL mode, platform and build options; never credentials.
    public string SettingsJson { get; set; } = "{}";
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
