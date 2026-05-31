namespace TaskForge.Observability.Api.Domain;

public sealed class PageView
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
