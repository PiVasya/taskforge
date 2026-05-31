namespace taskforge.Data.Models.Entities
{
    public sealed class UserFeatureRole
    {
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public Guid RoleId { get; set; }
        public FeatureRole Role { get; set; } = null!;

        public Guid? AssignedByUserId { get; set; }
        public User? AssignedByUser { get; set; }

        public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
