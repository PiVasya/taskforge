namespace taskforge.Data.Models.Entities
{
    public sealed class CourseOwner
    {
        public Guid CourseId { get; set; }
        public Course Course { get; set; } = null!;

        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public Guid AddedByUserId { get; set; }
    }
}
