using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Представляет награду/бейдж, который может быть присвоен пользователю.
    /// Каждый бейдж имеет имя, изображение и необязательное описание.
    /// Изображение хранится как строка:
    /// - либо относительный путь к файлу в wwwroot (например, "/badges/{id}.svg");
    /// - либо data URI (data:image/...;base64,...).
    /// </summary>
    public class Badge
    {
        /// <summary>
        /// Уникальный идентификатор бейджа.
        /// </summary>
        [Key]
        public Guid Id { get; set; }

        /// <summary>
        /// Человекочитаемое название бейджа.
        /// </summary>
        [Required]
        [MaxLength(128)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Необязательное текстовое описание.
        /// </summary>
        [MaxLength(1024)]
        public string? Description { get; set; }

        /// <summary>
        /// Ссылка на изображение бейджа:
        /// - либо относительный URL ("/badges/xxx.svg");
        /// - либо data URI вида "data:image/svg+xml;base64,...".
        ///
        /// Лимит поднят до 40000 символов, чтобы спокойно
        /// влезали большие SVG/base64.
        /// </summary>
        [Required]
        [MaxLength(40000)]
        public string ImageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Время создания бейджа (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
