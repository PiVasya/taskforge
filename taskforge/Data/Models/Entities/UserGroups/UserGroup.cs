using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public sealed class UserGroup
    {
        [Key] public Guid Id { get; set; }

        [Required, MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required, MaxLength(64)]
        public string Code { get; set; } = string.Empty;

        [MaxLength(2000)]
        public string? Description { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;

        [MaxLength(50)]
        public string? Color { get; set; }

        [MaxLength(50)]
        public string? Icon { get; set; }

        [MaxLength(200)]
        public string? ExternalId { get; set; }

        public string? Notes { get; set; }
        public string? TagsJson { get; set; }
    }
}
