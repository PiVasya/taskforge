using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Блок математического задания. Может быть как информационным, так и проверяемым.
    /// </summary>
    public sealed class TaskMathBlock
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid TaskAssignmentId { get; set; }

        public TaskAssignment TaskAssignment { get; set; } = null!;

        public int Order { get; set; }

        /// <summary>
        /// info | number | expression | set | single-choice | multi-choice | order | match
        /// </summary>
        [Required, MaxLength(40)]
        public string Kind { get; set; } = "info";

        /// <summary>Короткий заголовок / plain text.</summary>
        [Required, MaxLength(4000)]
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Rich content (Tiptap JSON string) для красивого условия, формул, картинок и файлов.
        /// </summary>
        public string? PromptContentJson { get; set; }

        /// <summary>
        /// JSON с параметрами конкретного вида блока.
        /// </summary>
        [Required]
        public string DataJson { get; set; } = "{}";

        public int Score { get; set; } = 1;
        public bool IsRequired { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
