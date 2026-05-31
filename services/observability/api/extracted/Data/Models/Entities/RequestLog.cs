using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Лог отдельного запроса к backend API. Нужен для админской аналитики:
/// нагрузка по маршрутам, ошибки, задержки, активность пользователей и клиентов.
/// Миграцию пользователь генерирует сам.
/// </summary>
public sealed class RequestLog
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    [Required, MaxLength(10)]
    public string Method { get; set; } = "GET";

    [Required, MaxLength(512)]
    public string Path { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? QueryString { get; set; }

    public int StatusCode { get; set; }
    public long DurationMs { get; set; }

    [MaxLength(64)]
    public string ClientType { get; set; } = "web";

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    public bool IsAuthenticated { get; set; }

    [MaxLength(2048)]
    public string? UserAgent { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
