using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Факт "первого входа" пользователя на Minecraft-сервер в конкретную неделю.
///
/// По твоей логике: если игрок зашёл хоть раз в неделю — считается 1 раз,
/// повторные входы на этой неделе не добавляют новых списаний.
/// </summary>
public sealed class MinecraftWeeklyJoin
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// Дата начала недели (понедельник) в UTC. Храним как date.
    /// </summary>
    public DateTime WeekStartUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public User? User { get; set; }
}
