using System;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public class User
    {
        [Key] public Guid Id { get; set; }

        [Required, EmailAddress, MaxLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string FirstName { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string LastName { get; set; } = string.Empty;

        // храните хеш и соль, а не пароль в открытом виде
        [Required] public byte[] PasswordHash { get; set; } = Array.Empty<byte>();
        [Required] public byte[] PasswordSalt { get; set; } = Array.Empty<byte>();

        public bool EmailConfirmed { get; set; } = false;
        public Guid EmailConfirmationToken { get; set; } = Guid.Empty;

        public Guid ResetPasswordToken { get; set; } = Guid.Empty;
        public DateTime? ResetPasswordExpiration { get; set; }

        public bool LockoutEnabled { get; set; } = false;
        public int AccessFailedCount { get; set; } = 0;
        public DateTime? LockoutEnd { get; set; }

        [MaxLength(50)] public string Role { get; set; } = "User";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastLoginAt { get; set; }

        [MaxLength(20)] public string? PhoneNumber { get; set; }
        public DateTime? DateOfBirth { get; set; }
        [MaxLength(2048)] public string? ProfilePictureUrl { get; set; }

        // поле для произвольных данных в формате JSON
        public string? AdditionalDataJson { get; set; }
    }
}
