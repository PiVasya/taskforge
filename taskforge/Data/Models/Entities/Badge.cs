using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Представляет награду/бейдж, который может быть присвоен пользователю. Каждый бейдж имеет
    /// имя, изображение (SVG или другая картинка) и необязательное описание. Картинка хранится
    /// как URL (например, путь к файлу в wwwroot или внешнему хранилищу).
    /// </summary>
    public class Badge
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(512)]
        public string? Description { get; set; }

        /// <summary>
        /// Ссылка на SVG или изображение бейджа. Путь может начинаться с '/', если хранится в
        /// статических файлах приложения (например, /badges/123.svg) или быть абсолютным URL.
        /// </summary>
        [Required]
        [MaxLength(2048)]
        public string ImageUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}