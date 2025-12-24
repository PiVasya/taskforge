using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Отдельное сообщение в обращении в службу поддержки.
    /// Может быть создано как пользователем, так и администратором.
    /// </summary>
    public class SupportMessage
    {
        /// <summary>
        /// Идентификатор сообщения.
        /// </summary>
        [Key]
        public Guid Id { get; set; }

        /// <summary>
        /// Идентификатор тикета, к которому относится сообщение.
        /// </summary>
        [Required]
        public Guid TicketId { get; set; }

        /// <summary>
        /// Навигационное свойство к тикету.
        /// </summary>
        public SupportTicket Ticket { get; set; } = null!;

        /// <summary>
        /// Пользовательский идентификатор автора внутри системы (если автор зарегистрирован).
        /// Для сообщений из Telegram или других каналов может быть null.
        /// </summary>
        public Guid? AuthorUserId { get; set; }

        /// <summary>
        /// Имя автора сообщения. Заполняется для администраторских сообщений из внешних каналов.
        /// </summary>
        [MaxLength(256)]
        public string? AuthorName { get; set; }

        /// <summary>
        /// Текст сообщения.
        /// </summary>
        [Required]
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Дата и время создания сообщения (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Признак, что сообщение было отправлено администратором.
        /// </summary>
        public bool IsFromAdmin { get; set; }

        /// <summary>
        /// Идентификатор чата в Telegram, если сообщение было отправлено или получено через Telegram.
        /// </summary>
        public long? TelegramChatId { get; set; }

        /// <summary>
        /// Идентификатор сообщения в Telegram, если применимо. Используется для поиска ответов.
        /// </summary>
        public long? TelegramMessageId { get; set; }

        /// <summary>
        /// Источник сообщения (например, SiteUser, SiteAdmin, TelegramAdmin).
        /// Позволяет в дальнейшем расширять каналы (Discord и т. д.).
        /// </summary>
        [MaxLength(64)]
        public string? Source { get; set; }
    }
}