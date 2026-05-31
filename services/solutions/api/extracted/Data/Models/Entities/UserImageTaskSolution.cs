using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Решение для image-test (как пробник, так и финальная отправка).
/// Храним ключи изображений в S3/MinIO и метаданные сравнения.
/// </summary>
public sealed class UserImageTaskSolution
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    [Required]
    public Guid TaskAssignmentId { get; set; }
    public TaskAssignment TaskAssignment { get; set; } = null!;

    /// <summary>
    /// "code" | "upload".
    /// </summary>
    [Required, MaxLength(16)]
    public string Kind { get; set; } = "code";

    /// <summary>
    /// Пробник (run-code) или финальная отправка.
    /// </summary>
    public bool IsTrial { get; set; }

    public string? SubmittedCode { get; set; }

    [MaxLength(50)]
    public string? Language { get; set; }

    public string? ReferenceKey { get; set; }

    /// <summary>
    /// Для upload: ключ загруженной картинки.
    /// Для code: ключ отрендеренной картинки.
    /// </summary>
    public string? SubmittedKey { get; set; }

    public double? SimilarityPercent { get; set; }
    public double? ThresholdPercent { get; set; }
    public bool? Passed { get; set; }

    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public string? RunnerError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
