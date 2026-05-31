namespace TaskForge.Files.Api.Domain;

public sealed class StoredFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
