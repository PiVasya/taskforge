using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO.TaskMaths
{
    public sealed class TaskMathOptionDto
    {
        public string Key { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public sealed class TaskMathMatchPairDto
    {
        public string LeftKey { get; set; } = "";
        public string RightKey { get; set; } = "";
    }

    public sealed class TaskMathSettingsDto
    {
        public int MaxAttempts { get; set; } = 1;
        public int PassPercent { get; set; } = 60;
        public bool ShuffleBlocks { get; set; } = false;
        public bool AllowReview { get; set; } = true;
        public List<int?> AttemptTimeLimitsSeconds { get; set; } = new();
    }

    public sealed class TaskMathBlockEditDto
    {
        public Guid Id { get; set; }
        public int Order { get; set; }
        public string Kind { get; set; } = "info";
        public string Prompt { get; set; } = "";
        public string? PromptContentJson { get; set; }
        public int Score { get; set; } = 1;
        public bool IsRequired { get; set; } = true;

        public List<TaskMathOptionDto>? Options { get; set; }
        public List<string>? CorrectOptionKeys { get; set; }

        public List<string>? AcceptedAnswers { get; set; }
        public bool? CaseSensitive { get; set; }
        public bool? Trim { get; set; }
        public double? NumericTolerance { get; set; }

        public List<string>? OrderItems { get; set; }
        public List<TaskMathOptionDto>? MatchLeftItems { get; set; }
        public List<TaskMathOptionDto>? MatchRightItems { get; set; }
        public List<TaskMathMatchPairDto>? MatchPairs { get; set; }
    }

    public sealed class TaskMathEditDto
    {
        public TaskMathSettingsDto Settings { get; set; } = new();
        public List<TaskMathBlockEditDto> Blocks { get; set; } = new();
    }

    public sealed class TaskMathBlockPublicDto
    {
        public Guid Id { get; set; }
        public int Order { get; set; }
        public string Kind { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string? PromptContentJson { get; set; }
        public int Score { get; set; }
        public bool IsRequired { get; set; }

        public List<TaskMathOptionDto>? Options { get; set; }
        public List<string>? OrderItems { get; set; }
        public List<TaskMathOptionDto>? MatchLeftItems { get; set; }
        public List<TaskMathOptionDto>? MatchRightItems { get; set; }
    }

    public sealed class TaskMathStartResponseDto
    {
        public Guid AttemptId { get; set; }
        public int AttemptNumber { get; set; }
        public int MaxAttempts { get; set; }
        public int PassPercent { get; set; }
        public int? TimeLimitSeconds { get; set; }
        public DateTime StartedAt { get; set; }
        public bool ShuffleBlocks { get; set; }
        public List<TaskMathBlockPublicDto> Blocks { get; set; } = new();
    }

    public sealed class TaskMathAnswerDto
    {
        public Guid BlockId { get; set; }
        public string? Text { get; set; }
        public List<string>? SelectedOptionKeys { get; set; }
        public List<string>? OrderedItems { get; set; }
        public List<TaskMathMatchPairDto>? MatchPairs { get; set; }
    }

    public sealed class TaskMathSubmitRequestDto
    {
        [Required]
        public Guid AttemptId { get; set; }

        public List<TaskMathAnswerDto> Answers { get; set; } = new();
    }

    public sealed class TaskMathSubmitResultDto
    {
        public Guid AttemptId { get; set; }
        public int AttemptNumber { get; set; }
        public int MaxAttempts { get; set; }
        public int PassPercent { get; set; }
        public int TotalScore { get; set; }
        public int EarnedScore { get; set; }
        public int ScorePercent { get; set; }
        public bool TimeExpired { get; set; }
        public bool Passed { get; set; }
    }
}
