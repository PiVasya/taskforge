using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO.Support
{
    /// <summary>
    /// Внутреннее событие: входящее сообщение от админа из внешнего канала (Telegram и т.п.).
    /// </summary>
    public class SupportNewEventRequestDto
    {
        [Required]
        public Guid TicketId { get; set; }

        [MaxLength(128)]
        public string AuthorName { get; set; } = string.Empty;

        [Required, MaxLength(2000)]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Необязательное внешнее id сообщения (например, telegram message_id) для дедупликации.
        /// </summary>
        public string? ExternalMessageId { get; set; }
    }
}
