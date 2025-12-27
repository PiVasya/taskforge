using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO.TaskTests
{
    /// <summary>
    /// Короткая запись о попытке (для раздела «Мои решения» / «Решения студентов»).
    /// </summary>
    public sealed class TaskTestAttemptListItemDto
    {
        public Guid AttemptId { get; set; }
        public Guid TaskAssignmentId { get; set; }
        public Guid CourseId { get; set; }

        public string CourseTitle { get; set; } = "";
        public string AssignmentTitle { get; set; } = "";

        public int AttemptNumber { get; set; }
        public DateTime SubmittedAt { get; set; }

        public int ScorePercent { get; set; }
        public bool Passed { get; set; }
        public bool TimeExpired { get; set; }

        /// <summary>
        /// Если false — студент не может открыть просмотр своей попытки.
        /// (Админ/преподаватель может.)
        /// </summary>
        public bool AllowReview { get; set; }
    }

    public sealed class TaskTestAttemptReviewQuestionDto
    {
        public Guid Id { get; set; }
        public int Order { get; set; }
        public string Type { get; set; } = "";
        public string Prompt { get; set; } = "";

        public List<TaskTestOptionDto>? Options { get; set; }

        // Для choice
        public List<string>? CorrectOptionKeys { get; set; }

        // Для fill/text
        public List<string>? AcceptedAnswers { get; set; }

        public TaskTestAnswerDto? UserAnswer { get; set; }

        public bool IsCorrect { get; set; }
    }

    /// <summary>
    /// Детали попытки для просмотра (включая ответы пользователя и правильные ответы).
    /// </summary>
    public sealed class TaskTestAttemptReviewDto
    {
        public Guid AttemptId { get; set; }
        public Guid TaskAssignmentId { get; set; }
        public Guid CourseId { get; set; }

        public string CourseTitle { get; set; } = "";
        public string AssignmentTitle { get; set; } = "";

        public Guid UserId { get; set; }
        public string? UserEmail { get; set; }

        public int AttemptNumber { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime SubmittedAt { get; set; }

        public int PassPercent { get; set; }
        public int TotalQuestions { get; set; }
        public int CorrectQuestions { get; set; }
        public int ScorePercent { get; set; }

        public bool Passed { get; set; }
        public bool TimeExpired { get; set; }

        public bool AllowReview { get; set; }

        public List<TaskTestAttemptReviewQuestionDto> Questions { get; set; } = new();
    }
}
