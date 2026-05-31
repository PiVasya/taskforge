using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public sealed class FeatureRole
    {
        [Key] public Guid Id { get; set; }

        [Required, MaxLength(64)]
        public string Code { get; set; } = string.Empty;

        [Required, MaxLength(128)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(1000)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public ICollection<UserFeatureRole> Users { get; set; } = new List<UserFeatureRole>();
    }
}
