using System.ComponentModel.DataAnnotations;
namespace taskforge.Data.Models.Entities
{
    public class TaskAssignment
    {
        [Key] public Guid Id { get; set; }

        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        // Подробное описание с форматированием (markdown/html)
        [Required]
        public string Description { get; set; } = string.Empty;

        [Required]
        public Guid CourseId { get; set; }
        public Course Course { get; set; } = null!;

        // Тип задания (пока только "code-test", позже "quiz", "match", ...)
        [Required, MaxLength(50)]
        public string Type { get; set; } = "code-test";

        public string? Tags { get; set; } // можно хранить через запятую
        public int Difficulty { get; set; } = 1; // 1=легко, 2=средне, 3=сложно

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<TaskTestCase> TestCases { get; set; } = new List<TaskTestCase>();
        public ICollection<UserTaskSolution> Solutions { get; set; } = new List<UserTaskSolution>();
    }
}