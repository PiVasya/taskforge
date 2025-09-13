using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public class RegisterUserDto
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, MaxLength(100)] public string FirstName { get; set; } = string.Empty;
        [Required, MaxLength(100)] public string LastName { get; set; } = string.Empty;
        [Required, MinLength(6)] public string Password { get; set; } = string.Empty;
        public string? PhoneNumber { get; set; }
        public DateTime? DateOfBirth { get; set; }
    }
}
