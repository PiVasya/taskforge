// taskforge/Data/Models/DTO/LeaderboardEntryDto.cs
using System;

namespace taskforge.Data.Models.DTO
{
    public sealed class LeaderboardEntryDto
    {
        public Guid UserId { get; set; }

        public int Rank { get; set; }

        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        public string? AvatarUrl { get; set; }

        public string? Location { get; set; }
        public string? Education { get; set; }

        public int SolvedAssignments { get; set; }
        public int TotalAttempts { get; set; }
        public DateTime? LastSubmitAt { get; set; }
    }
}
