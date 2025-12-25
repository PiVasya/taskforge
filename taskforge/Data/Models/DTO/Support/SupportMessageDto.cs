using System;

namespace taskforge.Data.Models.DTO.Support
{
    public class SupportMessageDto
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool IsFromAdmin { get; set; }
        public string AuthorName { get; set; } = string.Empty;
    }
}
