namespace TaskForge.Execution.Api.Domain;

public static class ExecutionJobKinds
{
    // Existing code/image jobs are not retrospectively guessed from Language or TestsJson.
    public const string Legacy = "legacy";
    public const string Code = "code";
    public const string Image = "image";
    public const string SqlCheck = "sql-check";
    public const string SqlPreview = "sql-preview";
    public const string SqlMaterialize = "sql-materialize";
}
