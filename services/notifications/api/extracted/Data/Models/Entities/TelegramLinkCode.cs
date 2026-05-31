using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Одноразовый код для привязки Telegram.
/// Код генерируется на сайте, пользователь отправляет его боту, бот подтверждает через внутренний API.
/// </summary>
public sealed class TelegramLinkCode
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA256(code + ":" + salt)</summary>
    [Required]
    public byte[] CodeHash { get; set; } = Array.Empty<byte>();

    [Required]
    public byte[] Salt { get; set; } = Array.Empty<byte>();

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UsedAtUtc { get; set; }

    // навигация не обязательна, но удобно
    public User? User { get; set; }
}
