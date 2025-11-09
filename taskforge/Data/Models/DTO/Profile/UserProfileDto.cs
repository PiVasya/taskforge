using System;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// Data transfer object representing user profile information returned to the client.
    /// Includes basic fields such as email, names, phone, date of birth, and profile photo URL.
    /// </summary>
    public class UserProfileDto
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public DateTime? DateOfBirth { get; set; }
        public string ProfilePictureUrl { get; set; } = string.Empty;
        public string AdditionalDataJson { get; set; } = string.Empty;
    }
}
