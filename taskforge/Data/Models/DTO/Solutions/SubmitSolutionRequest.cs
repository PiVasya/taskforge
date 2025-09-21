using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class SubmitSolutionRequest
    {
        [Required, MaxLength(50)]
        public string Language { get; set; } = "csharp";

        [Required]
        public string Code { get; set; } = string.Empty;
    }
}
