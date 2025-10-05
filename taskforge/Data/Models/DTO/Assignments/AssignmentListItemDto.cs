
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
    }
}