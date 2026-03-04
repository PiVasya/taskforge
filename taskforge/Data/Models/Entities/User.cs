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

        // ===== интеграции =====
        /// <summary>
        /// Telegram chat id (личный чат пользователя с ботом). Заполняется только после подтверждения кода.
        /// </summary>
        public long? TelegramChatId { get; set; }

        /// <summary>
        /// Telegram username без символа '@' (если есть).
        /// </summary>
        [MaxLength(64)]
        public string? TelegramUsername { get; set; }

        public DateTime? TelegramLinkedAtUtc { get; set; }

        /// <summary>
        /// Сколько раз пользователь успешно привязывал Telegram. Лимит = 2.
        /// </summary>
        public int TelegramLinkCount { get; set; } = 0;

        // ===== Minecraft =====
        /// <summary>
        /// Ник на Minecraft-сервере, привязанный к аккаунту TaskForge.
        /// </summary>
        [MaxLength(32)]
        public string? MinecraftNick { get; set; }

        /// <summary>
        /// UUID игрока (если сервер online-mode и мы получим его позже). Пока может быть null.
        /// </summary>
        [MaxLength(36)]
        public string? MinecraftUuid { get; set; }

        public DateTime? MinecraftLinkedAtUtc { get; set; }

        /// <summary>
        /// Сколько раз пользователь успешно привязывал Minecraft. Лимит = 2.
        /// </summary>
        public int MinecraftLinkCount { get; set; } = 0;

        // ===== группы / владельцы курсов =====
        public ICollection<UserGroupMember> GroupMembers { get; set; } = new List<UserGroupMember>();
        public ICollection<CourseOwner> OwnedCourses { get; set; } = new List<CourseOwner>();
    }
}
