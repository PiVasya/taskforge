namespace taskforge.Data.Models.DTO
{
    public sealed class AssignmentListItemDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; } // 👈 добавили, чтобы фронт видел описание
        public int Difficulty { get; set; }
        public string? Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool SolvedByCurrentUser { get; set; }
    }

    public sealed class AssignmentDetailsDto
    {
        public Guid Id { get; set; }
        public Guid CourseId { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int Difficulty { get; set; }
        public string? Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public int PublicTestCount { get; set; }
        public int HiddenTestCount { get; set; }
        public bool SolvedByCurrentUser { get; set; }
    }
}
