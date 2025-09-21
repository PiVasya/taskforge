using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class CreateCourseRequest
    {
        [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
        [Required] public string Description { get; set; } = string.Empty; // markdown/html
        public bool IsPublic { get; set; } = false;
    }

    public sealed class CourseListItemDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsPublic { get; set; }
        public Guid OwnerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int AssignmentCount { get; set; }
        public int SolvedCountForCurrentUser { get; set; } // прогресс
    }

    public sealed class CourseDetailsDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty; // markdown/html
        public bool IsPublic { get; set; }
        public Guid OwnerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public int AssignmentCount { get; set; }
        public int SolvedCountForCurrentUser { get; set; }
    }
}
