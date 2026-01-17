using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class UpdateCourseRequest
    {
        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        [MaxLength(4000)]
        public string? Description { get; set; }

        public bool IsPublic { get; set; }

        // полная замена набора групп (если null — не трогаем)
        public List<Guid>? VisibleGroupIds { get; set; }

        // полная замена набора owners (если null — не трогаем)
        public List<Guid>? OwnerIds { get; set; }
    }
}
