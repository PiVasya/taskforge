using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Обращение в поддержку. Хранит заголовок, тип и связь с пользователем.
    /// </summary>
    public class SupportTicket
    {
        [Key] public Guid Id { get; set; }

        /// <summary>
        /// Пользователь, который создал обращение.
        /// </summary>
        [Required]
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        /// <summary>
        /// Тип: bug, question, suggestion, other…
        /// </summary>
        [Required, MaxLength(32)]
        public string Type { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Закрыто ли обращение (например, решено).
        /// </summary>
        public bool IsClosed { get; set; } = false;

        public ICollection<SupportMessage> Messages { get; set; } = new List<SupportMessage>();
    }
}
