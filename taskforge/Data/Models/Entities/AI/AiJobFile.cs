using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiJobFile
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid JobId { get; set; }
    public AiJob Job { get; set; } = null!;

    [Required, MaxLength(1024)]
    public string FileKey { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? OriginalName { get; set; }

    [MaxLength(256)]
    public string? MimeType { get; set; }

    [MaxLength(2048)]
    public string? PublicUrl { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
