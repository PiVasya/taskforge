
namespace taskforge.Data.Models.DTO
{
    public sealed class AssignmentListItemDto
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Difficulty { get; set; }
        public string? Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool SolvedByCurrentUser { get; set; }
        public int Sort { get; set; }

        public bool CanEdit { get; set; }

        public bool IsHidden { get; set; }
        public string LifecycleStatus { get; set; } = "published";
        public bool IsAiDraft { get; set; }
        public Guid? SourceAgentRunId { get; set; }
        public Guid? SourceAgentArtifactId { get; set; }
        public int? SourceAgentTaskIndex { get; set; }
    }
}