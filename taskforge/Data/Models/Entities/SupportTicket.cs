using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Represents a support ticket created by a user.
    /// </summary>
    public class SupportTicket
    {
        /// <summary>
        /// Ticket identifier (GUID).
        /// </summary>
        [Key]
        public Guid Id { get; set; }

        /// <summary>
        /// User who created the ticket.
        /// </summary>
        [Required]
        public Guid UserId { get; set; }

        /// <summary>
        /// Type of ticket: bug, question, suggestion, etc.
        /// </summary>
        [Required]
        [MaxLength(32)]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Indicates if the ticket is closed.
        /// </summary>
        public bool IsClosed { get; set; } = false;

        /// <summary>
        /// When the ticket was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the ticket was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Collection of messages associated with this ticket.
        /// </summary>
        public ICollection<SupportMessage> Messages { get; set; } = new List<SupportMessage>();

        /// <summary>
        /// (Optional) The admin assigned to this ticket.
        /// </summary>
        public Guid? AssignedAdminId { get; set; }
    }
}