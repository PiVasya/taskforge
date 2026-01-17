namespace taskforge.Data.Models.Entities
{
    public sealed class UserGroupMember
    {
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public Guid GroupId { get; set; }
        public UserGroup Group { get; set; } = null!;

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    }
}
