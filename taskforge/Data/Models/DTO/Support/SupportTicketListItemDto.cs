using System;

namespace taskforge.Data.Models.DTO.Support
{
    public class SupportTicketListItemDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public bool IsClosed { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int MessagesCount { get; set; }

        // Short preview of last message (useful for list UI)
        public string? LastMessagePreview { get; set; }

        // Ticket owner (for admin/support UI). For a regular user
        // this will point to themselves.
        public SupportTicketUserDto? User { get; set; }
    }
}
