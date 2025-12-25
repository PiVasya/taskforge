using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO.Support
{
    public class CreateSupportTicketRequestDto
    {
        /// <summary>
        /// Тип обращения: bug, question, suggestion и т.п.
        /// </summary>
        [MaxLength(32)]
        public string? Type { get; set; }

        /// <summary>
        /// Первое сообщение пользователя.
        /// </summary>
        [Required, MaxLength(2000)]
        public string Message { get; set; } = string.Empty;
    }
}
