using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class ChangeEmailDto
    {
        [Required, EmailAddress, MaxLength(256)]
        public string NewEmail { get; set; } = string.Empty;

        /// <summary>Password of the current user to confirm identity.</summary>
        [Required]
        public string Password { get; set; } = string.Empty;
    }
}
