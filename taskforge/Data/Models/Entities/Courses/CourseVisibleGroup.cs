namespace taskforge.Data.Models.Entities
{
    public sealed class CourseVisibleGroup
    {
        public Guid CourseId { get; set; }
        public Course Course { get; set; } = null!;

        public Guid GroupId { get; set; }
        public UserGroup Group { get; set; } = null!;

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public Guid AddedByUserId { get; set; }
    }
}
