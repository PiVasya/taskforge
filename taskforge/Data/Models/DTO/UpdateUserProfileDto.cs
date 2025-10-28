using System;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// DTO used when updating the user's profile.
    /// All fields are optional and only provided properties will be updated.
    /// </summary>
    public class UpdateUserProfileDto
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? PhoneNumber { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public string? AdditionalDataJson { get; set; }
    }
}
