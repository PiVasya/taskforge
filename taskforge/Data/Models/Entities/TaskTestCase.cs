using System.ComponentModel.DataAnnotations;

public class TaskTestCase
{
    [Key] public Guid Id { get; set; }

    [Required]
    public Guid TaskAssignmentId { get; set; }
    public TaskAssignment TaskAssignment { get; set; } = null!;

    [Required]
    public string Input { get; set; } = string.Empty;

    [Required]
    public string ExpectedOutput { get; set; } = string.Empty;

    public bool IsHidden { get; set; } = false; // скрытые тесты для проверки «по-честному»
}
