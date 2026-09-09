namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>Mutable publication pointers for the one SQL aggregate of an assignment.</summary>
public sealed class SqlAssignmentSpec : ISqlMutableEntity
{
    public Guid AssignmentId { get; set; }
    public Guid? DraftVersionId { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
