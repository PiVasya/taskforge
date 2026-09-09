namespace TaskForge.Tasks.Api.Domain.Sql;

public sealed class SqlDataset : ISqlMutableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    // Identity is owned by identity-api; there is deliberately no cross-service FK.
    public Guid? OwnerUserId { get; set; }
    public string AccessScope { get; set; } = SqlDatasetScopes.Private;
    public bool IsArchived { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
