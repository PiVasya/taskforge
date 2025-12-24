using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Отдельное сообщение в обращении. Может быть от пользователя или от админа.
    /// </summary>
    public class SupportMessage
    {
        [Key] public Guid Id { get; set; }

        [Required]
        public Guid TicketId { get; set; }
        public SupportTicket Ticket { get; set; } = null!;

        /// <summary>
        /// Автор сообщения. Для системных или админ‑сообщений может быть null.
        /// </summary>
        public Guid? AuthorId { get; set; }
        public User? Author { get; set; }

        /// <summary>
        /// Имя автора (для админ‑сообщений, если нет связи с User).
        /// </summary>
        [MaxLength(256)]
        public string? AuthorName { get; set; }

        [Required]
        public string Text { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Сообщение от админа (владельца). Поле AuthorId при этом может быть null.
        /// </summary>
        public bool IsFromAdmin { get; set; } = false;

        /// <summary>
        /// ID сообщения в Telegram. Помогает связать ответ админа с конкретным пользовательским сообщением.
        /// </summary>
        public long? TelegramMessageId { get; set; }
    }
}
