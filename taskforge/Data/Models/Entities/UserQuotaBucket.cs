using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Токен‑бакет квота пользователя (ленивое восстановление токенов).
/// Храним 1 строку на (UserId, BucketType).
/// </summary>
public sealed class UserQuotaBucket
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    /// <summary>
    /// Тип бакета (например: "tasks", "top").
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string BucketType { get; set; } = string.Empty;

    /// <summary>
    /// Текущее число токенов.
    /// </summary>
    public int Tokens { get; set; }

    /// <summary>
    /// Время, относительно которого считаем восстановление токенов.
    /// Обновляется только когда действительно произошёл "refill".
    /// </summary>
    public DateTime LastRefillAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Оптимистическая конкуренция.
    /// </summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
