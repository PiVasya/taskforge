using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO.Support
{
    public class AddSupportMessageRequestDto
    {
        [Required, MaxLength(2000)]
        public string Message { get; set; } = string.Empty;
    }
}
