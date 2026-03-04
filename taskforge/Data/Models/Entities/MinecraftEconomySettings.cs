using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Настройки "экономики" Minecraft-интеграции.
/// Храним в БД (1 строка), чтобы можно было менять без правок кода.
/// </summary>
public sealed class MinecraftEconomySettings
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Сколько "рейтинга" списывается за неделю, если игрок хотя бы раз зашёл в эту неделю.
    /// По умолчанию: 70.
    /// </summary>
    public int WeeklyPenalty { get; set; } = 70;

    public DateTime UpdatedAtUtc { get; set; }
}
