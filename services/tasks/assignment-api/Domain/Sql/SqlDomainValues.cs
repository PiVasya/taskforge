namespace TaskForge.Tasks.Api.Domain.Sql;

public static class SqlTaskTypes
{
    public const string SqlTest = "sql-test";
}

public static class SqlTaskModes
{
    public const string Result = "result";
    public const string State = "state";
    public const string Schema = "schema";

    public static bool IsValid(string value) => value is Result or State or Schema;
}

public static class SqlDatasetScopes
{
    public const string Private = "private";
    public const string EditorLibrary = "editor-library";
}

public static class SqlValidationStatuses
{
    public const string Pending = "pending";
    public const string Validating = "validating";
    public const string Valid = "valid";
    public const string Invalid = "invalid";

    public static bool IsValid(string value) => value is Pending or Validating or Valid or Invalid;
}

public static class SqlEngineNames
{
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";
    public const string Sqlite = "sqlite";
}

/// <summary>Append-only authoritative content. Use a new version for any edit.</summary>
public interface ISqlImmutableEntity { }

/// <summary>Mutable pointers/receipts use optimistic concurrency, not wall-clock comparisons.</summary>
public interface ISqlMutableEntity
{
    Guid ConcurrencyStamp { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
