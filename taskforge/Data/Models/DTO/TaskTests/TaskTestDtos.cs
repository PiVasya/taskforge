using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO.TaskTests
{
    public sealed class TaskTestOptionDto
    {
        public string Key { get; set; } = "";
        public string Text { get; set; } = "";
    }

    // ===== Public (solve) =====
    public sealed class TaskTestQuestionPublicDto
    {
        public Guid Id { get; set; }
        public int Order { get; set; }
        public string Type { get; set; } = "";
        public string Prompt { get; set; } = "";

        // single-choice only
        public List<TaskTestOptionDto>? Options { get; set; }
    }

    public sealed class TaskTestStartResponseDto
    {
        public Guid AttemptId { get; set; }
        public int AttemptNumber { get; set; }
        public int MaxAttempts { get; set; }
        public int PassPercent { get; set; }
        public int? TimeLimitSeconds { get; set; }

        /// <summary>
        /// Время старта попытки (UTC). Нужно, чтобы корректно считать оставшееся время
        /// при перезагрузке страницы, если попытка уже начата.
        /// </summary>
        public DateTime StartedAt { get; set; }

        public bool ShuffleQuestions { get; set; }
        public bool ShuffleAnswers { get; set; }

        public List<TaskTestQuestionPublicDto> Questions { get; set; } = new();
    }

    public sealed class TaskTestAnswerDto
    {
        public Guid QuestionId { get; set; }
        public string? SelectedOptionKey { get; set; }
        /// <summary>
        /// Для вопросов с выбором (single-choice / multi-choice): выбранные ключи.
        /// Для single-choice можно передать 1 элемент.
        /// </summary>
        public List<string>? SelectedOptionKeys { get; set; }
        public string? Text { get; set; }
    }

    public sealed class TaskTestSubmitRequestDto
    {
        [Required]
        public Guid AttemptId { get; set; }

        public List<TaskTestAnswerDto> Answers { get; set; } = new();
    }

    public sealed class TaskTestSubmitResultDto
    {
        public Guid AttemptId { get; set; }
        public int AttemptNumber { get; set; }
        public int MaxAttempts { get; set; }
        public int PassPercent { get; set; }

        public int TotalQuestions { get; set; }
        public int CorrectQuestions { get; set; }
        public int ScorePercent { get; set; }

        public bool TimeExpired { get; set; }
        public bool Passed { get; set; }
    }

    // ===== Editor (create/edit test) =====
    public sealed class TaskTestSettingsDto
    {
        public int MaxAttempts { get; set; } = 1;
        public int PassPercent { get; set; } = 60;
        public bool ShuffleQuestions { get; set; } = true;
        public bool ShuffleAnswers { get; set; } = true;
        /// <summary>
        /// Разрешить студентам просмотр своих попыток после сдачи.
        /// </summary>
        public bool AllowReview { get; set; } = true;
        public List<int?> AttemptTimeLimitsSeconds { get; set; } = new();
    }

    public sealed class TaskTestQuestionEditDto
    {
        public Guid Id { get; set; }
        public int Order { get; set; }

        /// <summary>"single-choice" | "multi-choice" | "fill" | "text"</summary>
        public string Type { get; set; } = "single-choice";

        public string Prompt { get; set; } = "";

        // single-choice
        public List<TaskTestOptionDto>? Options { get; set; }
        public List<string>? CorrectOptionKeys { get; set; }

        // fill/text
        public List<string>? AcceptedAnswers { get; set; }
        public bool? CaseSensitive { get; set; }
        public bool? Trim { get; set; }
    }

    public sealed class TaskTestEditDto
    {
        public TaskTestSettingsDto Settings { get; set; } = new();
        public List<TaskTestQuestionEditDto> Questions { get; set; } = new();
    }
}
