using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Связующая таблица между пользователями и их бейджами. Позволяет присваивать
    /// нескольким пользователям один и тот же бейдж, а также хранить дату награждения.
    /// </summary>
    public class UserBadge
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid UserId { get; set; }

        [Required]
        public Guid BadgeId { get; set; }

        public DateTime AwardedAt { get; set; } = DateTime.UtcNow;

        // Навигационные свойства (опционально), чтобы Entity Framework мог строить связи
        public User? User { get; set; }
        public Badge? Badge { get; set; }
    }
}