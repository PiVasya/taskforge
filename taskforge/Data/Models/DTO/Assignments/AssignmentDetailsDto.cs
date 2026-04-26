using taskforge.Data.Models.DTO;
namespace taskforge.Data.Models.DTO
{

    public sealed class AssignmentDetailsDto
    {
        public Guid Id { get; set; }
        public Guid CourseId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int Difficulty { get; set; }
        public int Rating { get; set; }
        public string? Tags { get; set; }
        public string Type { get; set; } = "code-test";
        public List<string> AllowedLanguages { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public int PublicTestCount { get; set; }
        public int HiddenTestCount { get; set; }
        public bool SolvedByCurrentUser { get; set; }
        public List<AssignmentTestCaseDto> TestCases { get; set; } = new();
        public int Sort { get; set; }

        // --- Code policy (applies to code-test and image-test code part) ---
        public List<string> CodeRequiredCalls { get; set; } = new();
        public List<string> CodeForbiddenCalls { get; set; } = new();

        // image-test
        public string? ImageTestReferenceKey { get; set; }
        public double? ImageTestSimilarityThreshold { get; set; }

        public bool CanEdit { get; set; }   // <--- НОВОЕ
    }
}