using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// Сводка по решению пользователя, используется в списках.
    /// Содержит дату отправки, название курса и задания, язык и количество пройденных/проваленных тестов.
    /// </summary>
    public sealed class SolutionListItemDto
    {
        public Guid Id { get; set; }
        public DateTime SubmittedAt { get; set; }
        public string CourseTitle { get; set; } = string.Empty;
        public string AssignmentTitle { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public bool PassedAllTests { get; set; }
        public int PassedCount { get; set; }
        public int FailedCount { get; set; }
    }

    /// <summary>
    /// Детальная информация о решении пользователя, включая код и результаты по каждому тесту.
    /// Используется при просмотре конкретного решения.
    /// </summary>
    public sealed class SolutionDetailsDto
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public Guid TaskAssignmentId { get; set; }
        public string SubmittedCode { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public bool PassedAllTests { get; set; }
        public int PassedCount { get; set; }
        public int FailedCount { get; set; }
        public DateTime SubmittedAt { get; set; }
        public string CourseTitle { get; set; } = string.Empty;
        public string AssignmentTitle { get; set; } = string.Empty;
        public IList<SolutionCaseResultDto>? Cases { get; set; }
    }
}
