using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Одноразовый код для привязки Minecraft.
/// Сценарий:
/// 1) Пользователь на сайте вводит ник.
/// 2) TaskForge генерирует код и (позже) отправляет его на Minecraft-сервер.
/// 3) Игрок получает код в игре и вводит его на сайте.
/// </summary>
public sealed class MinecraftLinkCode
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// Ник, который пользователь пытается привязать.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string Nick { get; set; } = string.Empty;

    /// <summary>SHA256(code + ":" + salt)</summary>
    [Required]
    public byte[] CodeHash { get; set; } = Array.Empty<byte>();

    [Required]
    public byte[] Salt { get; set; } = Array.Empty<byte>();

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UsedAtUtc { get; set; }

    public User? User { get; set; }
}
