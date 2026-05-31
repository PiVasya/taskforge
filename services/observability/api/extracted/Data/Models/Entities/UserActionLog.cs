using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Подробный журнал действий пользователей. Нужен для админского просмотра
/// всех действий: входы, переходы по страницам, вызовы API, отправки решений,
/// сохранения заданий и другие события.
/// </summary>
public sealed class UserActionLog
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    [Required, MaxLength(64)]
    public string Category { get; set; } = "general";

    [Required, MaxLength(128)]
    public string ActionType { get; set; } = "request";

    [MaxLength(32)]
    public string Source { get; set; } = "api";

    [MaxLength(16)]
    public string? Method { get; set; }

    [MaxLength(512)]
    public string? Path { get; set; }

    [MaxLength(512)]
    public string? Target { get; set; }

    [MaxLength(512)]
    public string? Description { get; set; }

    public int? StatusCode { get; set; }

    [MaxLength(2048)]
    public string? MetadataJson { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    [MaxLength(2048)]
    public string? UserAgent { get; set; }

    public bool IsAuthenticated { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
