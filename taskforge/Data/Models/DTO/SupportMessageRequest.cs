using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// Данные, которые пользователь отправляет в службу поддержки.
    /// </summary>
    public class SupportMessageDto
    {
        /// <summary>
        /// Тип обращения: bug, question, suggestion и т. д.
        /// </summary>
        [Required, MaxLength(32)]
        public string Type { get; set; } = "question";

        /// <summary>
        /// Текст сообщения (максимум 2000 символов).
        /// </summary>
        [Required, MaxLength(2000)]
        public string Message { get; set; } = string.Empty;
    }
}
