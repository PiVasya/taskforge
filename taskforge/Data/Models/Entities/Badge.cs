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
        /// Ссылка или data URI на изображение бейджа. Раньше здесь хранился путь к
        /// файлу внутри каталога /badges, но теперь изображения сохраняются как
        /// data URI прямо в базе данных. Тем не менее поле остаётся строковым
        /// и поддерживает до 8192 символов, чтобы уместить длинные base64‑строки.
        /// </summary>
        [Required]
        [MaxLength(8192)]
        public string ImageUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}