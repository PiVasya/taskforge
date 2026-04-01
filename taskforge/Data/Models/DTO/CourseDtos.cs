using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class CreateCourseRequest
    {
        [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
        [Required] public string Description { get; set; } = string.Empty; // markdown/html
        public bool IsPublic { get; set; } = false;

        // если IsPublic=false, тоVisibleGroupIds ограничивает видимость курса
        public List<Guid>? VisibleGroupIds { get; set; }

        // владельцы курса (доп. owners). Создатель всегда добавляется автоматически.
        public List<Guid>? OwnerIds { get; set; }
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

        public int TestCount { get; set; }
        public int SolvedTestsCountForCurrentUser { get; set; }
        public int MathCount { get; set; }
        public int SolvedMathCountForCurrentUser { get; set; }

        public bool IsCompletedForCurrentUser { get; set; }

        public bool CanEdit { get; set; }

        public List<Guid> VisibleGroupIds { get; set; } = new();
        public List<Guid> OwnerIds { get; set; } = new();
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

        public int TestCount { get; set; }
        public int SolvedTestsCountForCurrentUser { get; set; }
        public int MathCount { get; set; }
        public int SolvedMathCountForCurrentUser { get; set; }

        public bool IsCompletedForCurrentUser { get; set; }

        public bool CanEdit { get; set; }

        public List<Guid> VisibleGroupIds { get; set; } = new();
        public List<Guid> OwnerIds { get; set; } = new();
    }
}
