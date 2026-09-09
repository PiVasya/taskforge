using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TaskForge.Sql;

// Versioned wire contracts; these are not EF entities.
public static class SqlWire
{
    public const int Version = 1;
    public const string AdapterVersion = "1.0.0";
    public const int MaxSourceLength = 100_000;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 48
    };
    public static JsonElement Object() => JsonSerializer.SerializeToElement(new { }, Json);
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Read<T>(JsonElement element) => element.Deserialize<T>(Json)
        ?? throw new ArgumentException("A JSON document is required.");
    public static bool IsSqlKind(string? kind) => kind is "sql-check" or "sql-preview" or "sql-materialize";
    public static bool IsHash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsTerminal(string? status) => status is not null && status is not ("queued" or "running");
}

public sealed record SqlLimits(int TimeoutMs = 5000, int MaxRows = 1000, int PreviewRows = 200,
    int MaxBytes = 1_048_576, int MaxStatements = 20)
{
    public void Validate()
    {
        if (TimeoutMs is < 100 or > 10_000 || MaxRows is < 1 or > 1000 || PreviewRows is < 1 or > 200
            || PreviewRows > MaxRows || MaxBytes is < 1024 or > 2_097_152 || MaxStatements is < 1 or > 50)
            throw new ArgumentException("SQL limits exceed the supported execution budget.");
    }
}

public sealed record SqlComparison(bool OrderMatters = false, bool ColumnNamesMatter = true,
    bool DuplicatesMatter = true, bool CaseSensitive = true, decimal NumericTolerance = 0)
{
    public void Validate()
    {
        if (NumericTolerance is < 0 or > 1_000_000) throw new ArgumentException("Invalid numeric tolerance.");
    }
}

public sealed record SqlStateCheck(string[]? Tables = null);
public sealed record SqlSchemaCheck(string[]? Tables = null, bool ConstraintNamesMatter = false,
    bool DefaultsMatter = true, bool IndexesMatter = true);

public sealed record SqlDefault(string Kind = "literal", JsonElement? Value = null);
public sealed record SqlColumn(string Name, string Type = "integer", bool Nullable = false,
    int? Length = null, int? Precision = null, int? Scale = null, bool Identity = false, SqlDefault? Default = null);
public sealed record SqlForeignKey(string Name, string[] Columns, string ReferenceTable,
    string[] ReferenceColumns, string OnDelete = "no_action", string OnUpdate = "no_action");
public sealed record SqlIndex(string Name, string[] Columns, bool Unique = false);
public sealed record SqlTable(string Name, SqlColumn[] Columns, string[]? PrimaryKey = null,
    string[][]? Unique = null, SqlForeignKey[]? ForeignKeys = null, SqlIndex[]? Indexes = null);
public sealed record SqlDefinition(SqlTable[] Tables);
public sealed record SqlColumnMapping(string? Type = null, SqlDefault? Default = null);
public sealed record SqlEngineMapping(Dictionary<string, SqlColumnMapping>? Columns = null);

public sealed record SqlProfile(Guid Id, string Key, string DisplayName, string Engine, string EngineVersion,
    string RuntimeDigest, string AdapterVersion, JsonElement Settings, string Fingerprint);
public sealed record SqlProfileRegistration(string Engine, string EngineVersion, string RuntimeDigest,
    string AdapterVersion, JsonElement Settings);

public sealed record SqlTargetInput(Guid EngineProfileId, bool Enabled = true, int Sort = 0,
    string? StarterSqlOverride = null, string? ReferenceSqlOverride = null,
    SqlComparison? ResultComparisonSettingsOverride = null, SqlStateCheck? StateCheckSettingsOverride = null,
    SqlSchemaCheck? SchemaCheckSettingsOverride = null);
public sealed record SqlSpecInput(Guid DatasetVersionId, string Mode, string? StarterSql, string? ReferenceSql,
    bool AllowMultipleStatements, SqlComparison ResultComparisonSettings, SqlStateCheck StateCheckSettings,
    SqlSchemaCheck SchemaCheckSettings, SqlLimits Limits, SqlTargetInput[] Targets, Guid? ConcurrencyStamp = null);
public sealed record SqlDatasetInput(string Name, string? Description, string AccessScope = "private");
public sealed record SqlDatasetUpdate(string Name, string? Description, string AccessScope, bool IsArchived, Guid ConcurrencyStamp);
public sealed record SqlDatasetVersionInput(SqlDefinition Definition,
    Dictionary<string, List<Dictionary<string, JsonElement>>> Seed,
    Dictionary<string, SqlEngineMapping>? EngineOverrides = null, Guid? ConcurrencyStamp = null);
public sealed record SqlPublishInput(Guid VersionId, Guid ConcurrencyStamp);

public sealed class SqlJobPayload
{
    public int ContractVersion { get; set; } = SqlWire.Version;
    public Guid AssignmentId { get; set; }
    public Guid SpecVersionId { get; set; }
    public Guid DatasetVersionId { get; set; }
    public Guid EngineTargetId { get; set; }
    public Guid? ValidationRunId { get; set; }
    public SqlProfile Profile { get; set; } = null!;
    public string MaterializationKey { get; set; } = "";
    public string ArtifactKey { get; set; } = "";
    public string DatasetHash { get; set; } = "";
    public SqlDefinition Definition { get; set; } = null!;
    public Dictionary<string, List<Dictionary<string, JsonElement>>> Seed { get; set; } = new();
    public Dictionary<string, SqlEngineMapping> EngineOverrides { get; set; } = new();
    public string Mode { get; set; } = "result";
    public string Source { get; set; } = "";
    public string? ReferenceSql { get; set; }
    public bool AllowMultipleStatements { get; set; }
    public SqlComparison Comparison { get; set; } = new();
    public SqlStateCheck StateCheck { get; set; } = new();
    public SqlSchemaCheck SchemaCheck { get; set; } = new();
    public SqlLimits Limits { get; set; } = new();
    public JsonElement? Expected { get; set; }
    public string? ExpectedContentHash { get; set; }
}

public sealed record SqlCreateJob(string Kind, string DeduplicationKey, Guid? SubmissionId,
    Guid? UserId, SqlJobPayload Payload);
public sealed record SqlClaim(string WorkerId, string[] Kinds, string[] Targets);
public sealed record SqlLease(string WorkerId, Guid LeaseToken);
public sealed record SqlComplete(string WorkerId, Guid LeaseToken, string Verdict, bool Passed,
    long DurationMs, JsonElement Result);
public sealed record SqlJobView(Guid Id, Guid? SubmissionId, Guid? UserId, string Kind, string Status,
    Guid? LeaseToken, DateTimeOffset? LeaseExpiresAt, SqlJobPayload Payload, JsonElement? Result = null, DateTimeOffset? CreatedAt = null);
public sealed record SqlCapabilities(string WorkerId, string[] Targets, int Concurrency = 2);
public sealed record SqlAttemptInput(Guid AssignmentId, Guid EngineProfileId, string Sql, Guid RequestId);

public static class SqlHttp
{
    public static string Url(IConfiguration cfg, string service, string fallback)
        => (cfg[$"Services:{service}"] ?? cfg[$"ServiceUrls:{service}"] ?? fallback).TrimEnd('/');
    public static async Task<T?> Send<T>(IHttpClientFactory factory, IConfiguration cfg, HttpMethod method,
        string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Internal-Key", cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"]
            ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY"));
        if (body is not null) request.Content = JsonContent.Create(body, options: SqlWire.Json);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await factory.CreateClient().SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web), deadline.Token);
    }
}

public static class SqlPortableValidator
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{0,47}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "integer", "bigint", "decimal", "string", "text", "boolean", "date", "datetime", "uuid", "binary" };
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
        { "select", "from", "where", "group", "order", "table", "index", "primary", "foreign", "key", "constraint", "references", "user", "values", "default", "null", "check", "limit", "offset", "join", "union", "with", "recursive", "create", "drop", "alter", "database", "schema", "view", "trigger", "returning" };
    public static void Identifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !NamePattern.IsMatch(name) || Reserved.Contains(name)
            || name.StartsWith("sqlite_", StringComparison.Ordinal) || name.StartsWith("tfq_", StringComparison.Ordinal))
            throw new ArgumentException($"Invalid portable SQL identifier: {name}.");
    }
    public static void Definition(SqlDatasetVersionInput input)
    {
        if (input.Definition?.Tables is null || input.Seed is null) throw new ArgumentException("Definition and seed are required.");
        var tables = input.Definition.Tables;
        if (tables.Length > 24) throw new ArgumentException("At most 24 tables are supported.");
        if (tables.Any(x => x is null || x.Columns is null || x.Columns.Any(c => c is null))) throw new ArgumentException("Tables and columns cannot be null.");
        var tableNames = tables.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (tableNames.Count != tables.Length) throw new ArgumentException("Duplicate table names.");
        var totalRows = 0;
        foreach (var table in tables)
        {
            Identifier(table.Name);
            if (table.Columns is null || table.Columns.Length is < 1 or > 64) throw new ArgumentException("A table needs 1 to 64 columns.");
            var names = table.Columns.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Count != table.Columns.Length) throw new ArgumentException("Duplicate column names.");
            foreach (var column in table.Columns)
            {
                Identifier(column.Name);
                if (!Types.Contains(column.Type)) throw new ArgumentException($"Unsupported logical type: {column.Type}.");
                if (column.Type == "string" && (column.Length is null or < 1 or > 4000)) throw new ArgumentException("String length must be 1..4000.");
                if (column.Type == "decimal" && (column.Precision is null or < 1 or > 28 || column.Scale is null or < 0 || column.Scale > column.Precision)) throw new ArgumentException("Decimal needs precision 1..28 and scale 0..precision.");
                if (column.Identity && (column.Type != "integer" || column.Nullable || (table.PrimaryKey?.Length ?? 0) != 1 || table.PrimaryKey![0] != column.Name)) throw new ArgumentException("Portable identity must be the single non-null integer primary key.");
                if (column.Identity && column.Default is not null) throw new ArgumentException("Identity cannot have an additional default.");
                ValidateDefault(column.Default);
            }
            void Columns(IEnumerable<string>? columns)
            {
                var list = columns?.ToArray() ?? [];
                if (list.Length == 0 || list.Distinct(StringComparer.Ordinal).Count() != list.Length || list.Any(x => !names.Contains(x))) throw new ArgumentException($"Invalid columns in {table.Name} constraint.");
            }
            if (table.PrimaryKey is { Length: > 0 })
            {
                Columns(table.PrimaryKey);
                if (table.Columns.Any(x => table.PrimaryKey.Contains(x.Name) && x.Nullable)) throw new ArgumentException("Primary-key columns cannot be nullable.");
            }
            foreach (var unique in table.Unique ?? []) Columns(unique);
            foreach (var index in table.Indexes ?? []) { if (index is null) throw new ArgumentException("Indexes cannot be null."); Identifier(index.Name); Columns(index.Columns); }
            foreach (var fk in table.ForeignKeys ?? [])
            {
                if (fk is null) throw new ArgumentException("Foreign keys cannot be null.");
                Identifier(fk.Name); Columns(fk.Columns);
                if (!tableNames.Contains(fk.ReferenceTable) || fk.ReferenceColumns is null || fk.ReferenceColumns.Length != fk.Columns.Length) throw new ArgumentException("Invalid foreign-key target.");
                var other = tables.First(x => x.Name == fk.ReferenceTable);
                if (fk.ReferenceColumns.Any(x => !other.Columns.Any(c => c.Name == x))) throw new ArgumentException("Unknown foreign-key column.");
                var uniqueSets = (other.Unique ?? []).Concat(new[] { other.PrimaryKey ?? [] });
                if (!uniqueSets.Any(x => x is not null && x.SequenceEqual(fk.ReferenceColumns))) throw new ArgumentException("Foreign keys must reference a primary or unique key.");
                if (!ValidAction(fk.OnDelete) || !ValidAction(fk.OnUpdate)) throw new ArgumentException("Unsupported foreign-key action.");
                if ((fk.OnDelete == "set_null" || fk.OnUpdate == "set_null") && fk.Columns.Any(x => !table.Columns.First(c => c.Name == x).Nullable)) throw new ArgumentException("SET NULL needs nullable referencing columns.");
            }
            var rows = input.Seed.GetValueOrDefault(table.Name) ?? [];
            if (rows.Count > 1000) throw new ArgumentException("At most 1000 seed rows per table.");
            totalRows += rows.Count;
            foreach (var row in rows)
            {
                if (row is null) throw new ArgumentException("Seed rows cannot be null.");
                if (row.Keys.Any(x => !names.Contains(x))) throw new ArgumentException("Unknown seed column.");
                foreach (var column in table.Columns)
                {
                    if (!row.TryGetValue(column.Name, out var value))
                    {
                        if (column.Default?.Kind is "current_timestamp" or "current_date") throw new ArgumentException("Seed rows must explicitly supply values for time-dependent defaults.");
                        if (!column.Nullable && !column.Identity && column.Default is null) throw new ArgumentException($"Missing seed value: {table.Name}.{column.Name}.");
                    }
                    else if (value.ValueKind == JsonValueKind.Null && !column.Nullable) throw new ArgumentException("NULL seed value for a non-null column.");
                    else if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 65536)) throw new ArgumentException("Seed cells must be bounded scalar values.");
                }
            }
        }
        if (totalRows > 5000 || input.Seed.Keys.Any(x => !tableNames.Contains(x))) throw new ArgumentException("Invalid seed table or total row limit exceeded.");
        foreach (var mapping in input.EngineOverrides ?? [])
        {
            if (mapping.Key is not ("postgresql" or "mysql" or "sqlite")) throw new ArgumentException("Unsupported override engine.");
            if (mapping.Value is null) throw new ArgumentException("Engine mappings cannot be null.");
            foreach (var item in mapping.Value.Columns ?? [])
            {
                if (item.Value is null) throw new ArgumentException("Column mappings cannot be null.");
                var parts = item.Key.Split('.');
                if (parts.Length != 2 || !tables.Any(t => t.Name == parts[0] && t.Columns.Any(c => c.Name == parts[1]))) throw new ArgumentException("Unknown mapped column.");
                if (item.Value.Type is { } type && !Regex.IsMatch(type, "^[a-z][a-z0-9 ]{0,31}(\\([0-9]{1,4}(,[0-9]{1,2})?\\))?$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid engine type mapping.");
                ValidateDefault(item.Value.Default);
            }
        }
        if (SqlWire.Serialize(input).Length > 2_000_000) throw new ArgumentException("Dataset exceeds the 2 MB authoring limit.");
    }
    public static void Spec(SqlSpecInput spec)
    {
        if (spec.DatasetVersionId == Guid.Empty || spec.Mode is not ("result" or "state" or "schema")) throw new ArgumentException("Dataset version and SQL verification mode are required.");
        if (spec.Limits is null || spec.ResultComparisonSettings is null || spec.StateCheckSettings is null || spec.SchemaCheckSettings is null)
            throw new ArgumentException("SQL limits and check settings are required.");
        spec.Limits.Validate(); spec.ResultComparisonSettings.Validate();
        if ((spec.StarterSql?.Length ?? 0) > SqlWire.MaxSourceLength || (spec.ReferenceSql?.Length ?? 0) > SqlWire.MaxSourceLength) throw new ArgumentException("SQL source is too long.");
        if (spec.Targets is null || spec.Targets.Length is < 1 or > 12 || !spec.Targets.Any(x => x is not null && x.Enabled)) throw new ArgumentException("Select at least one engine.");
        if (spec.Targets.Any(x => x is null)) throw new ArgumentException("Engine targets cannot be null.");
        if (spec.Targets.Select(x => x.EngineProfileId).Distinct().Count() != spec.Targets.Length) throw new ArgumentException("Duplicate engine profile.");
        foreach (var target in spec.Targets)
        {
            if (target.EngineProfileId == Guid.Empty || target.Sort < 0) throw new ArgumentException("Invalid engine target.");
            if ((target.StarterSqlOverride?.Length ?? 0) > SqlWire.MaxSourceLength || (target.ReferenceSqlOverride?.Length ?? 0) > SqlWire.MaxSourceLength) throw new ArgumentException("Engine SQL source is too long.");
            target.ResultComparisonSettingsOverride?.Validate();
            foreach (var table in (target.StateCheckSettingsOverride?.Tables ?? []).Concat(target.SchemaCheckSettingsOverride?.Tables ?? [])) Identifier(table);
        }
        foreach (var table in (spec.StateCheckSettings.Tables ?? []).Concat(spec.SchemaCheckSettings.Tables ?? [])) Identifier(table);
    }
    private static bool ValidAction(string action) => action is "no_action" or "restrict" or "cascade" or "set_null";
    private static void ValidateDefault(SqlDefault? value)
    {
        if (value is null) return;
        if (value.Kind is not ("literal" or "current_timestamp" or "current_date") || (value.Kind == "literal" && (value.Value is null || value.Value.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined))) throw new ArgumentException("Use a literal or semantic date/time default.");
    }
}
