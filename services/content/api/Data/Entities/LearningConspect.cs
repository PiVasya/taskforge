namespace LearningContentService.Data.Entities;

public sealed class LearningConspect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }

    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? Lead { get; set; }

    // Дублируем учебные коды для быстрых фильтров без обязательного join по Course.
    public string? SubjectCode { get; set; }
    public string? ExamCode { get; set; }
    public string? SectionCode { get; set; }

    // note, trainer, exam-review, dictionary, mixed etc.
    public string Kind { get; set; } = "conspect";

    // Черновик/публикация отделены от структуры курса.
    public bool IsPublished { get; set; } = true;
    public int SortOrder { get; set; }
    public int EstimatedMinutes { get; set; } = 10;

    // Например: A1, Орфография, Сложность 1/5. Храним JSON, чтобы не плодить мелкие поля.
    public string? BadgesJson { get; set; }

    // Главное поле. Здесь лежит полноценный конспект уровня HTML-прототипа:
    // вкладки, карточки правил, предупреждения, примеры, словари, слова по годам, тренировки.
    public string ContentJson { get; set; } = "{}";

    // Дополнительный plain-text для поиска/индексации.
    public string? SearchText { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
