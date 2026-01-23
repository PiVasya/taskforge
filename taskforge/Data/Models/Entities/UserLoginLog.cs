using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Represents a record of a user login event. Stores the time of login and
    /// additional context such as IP address and user agent. This entity
    /// enables auditing of account activity and supports security analysis.
    /// </summary>
    public class UserLoginLog
    {
        /// <summary>
        /// Primary key for the log entry.
        /// </summary>
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Identifier of the user who logged in.
        /// </summary>
        [Required]
        public Guid UserId { get; set; }

        /// <summary>
        /// UTC timestamp of when the login occurred.
        /// </summary>
        [Required]
        public DateTime LoginAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Remote IP address of the client at the time of login. Can be null if
        /// unavailable.
        /// </summary>
        [MaxLength(45)]
        public string? IpAddress { get; set; }

        /// <summary>
        /// Raw User‑Agent header provided by the client. Useful for determining
        /// browser and operating system.
        /// </summary>
        [MaxLength(2048)]
        public string? UserAgent { get; set; }

        /// <summary>
        /// Parsed device or OS information derived from the User‑Agent string.
        /// Optional and may be populated by services that understand UA strings.
        /// </summary>
        [MaxLength(512)]
        public string? DeviceInfo { get; set; }

        /// <summary>
        /// Navigation property to the associated user.
        /// </summary>
        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }
    }
}