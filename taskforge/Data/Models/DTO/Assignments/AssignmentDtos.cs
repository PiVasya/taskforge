using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO
{
    public sealed class AssignmentListItemDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public int Difficulty { get; set; }
        public string? Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool SolvedByCurrentUser { get; set; }
    }

    public sealed class AssignmentDetailsDto
    {
        public Guid Id { get; set; }
        public Guid CourseId { get; set; }

        public string Title { get; set; } = "";
        public string Description { get; set; } = "";

        public string Type { get; set; } = "code-test";  // <-- нужно фронту
        public int Difficulty { get; set; }
        public string? Tags { get; set; }
        public DateTime CreatedAt { get; set; }

        public int PublicTestCount { get; set; }
        public int HiddenTestCount { get; set; }
        public bool SolvedByCurrentUser { get; set; }

        // <-- ключевое: отдать тесты на фронт
        public List<AssignmentTestCaseDto> TestCases { get; set; } = new();
    }

    public sealed class AssignmentTestCaseDto
    {
        public Guid Id { get; set; }
        public string Input { get; set; } = "";
        public string ExpectedOutput { get; set; } = "";
        public bool IsHidden { get; set; }
    }
}
