namespace TaskForge.Observability.Api.Domain;

public sealed class PageView
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? Action { get; set; }
    public int? StatusCode { get; set; }
    public long? DurationMs { get; set; }
    public string? UserAgent { get; set; }
    public string? Source { get; set; }
    public string? TraceId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ClientIpHash { get; set; }
    public string? ClientIpPrefix { get; set; }
    public string? ClientCountry { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
