using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public sealed class MinecraftChatMessage
    {
        [Key] public Guid Id { get; set; }
        public Guid? UserId { get; set; }
        public User? User { get; set; }

        [MaxLength(64)]
        public string Source { get; set; } = "SiteUser";

        [MaxLength(64)]
        public string? AuthorName { get; set; }

        [MaxLength(32)]
        public string? MinecraftNick { get; set; }

        [MaxLength(64)]
        public string? MinecraftUuid { get; set; }

        [Required, MaxLength(2000)]
        public string Message { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
