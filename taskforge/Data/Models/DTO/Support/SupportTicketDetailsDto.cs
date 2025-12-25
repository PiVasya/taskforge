using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO.Support
{
    public class SupportTicketDetailsDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsClosed { get; set; }
        public List<SupportMessageDto> Messages { get; set; } = new();
    }
}
