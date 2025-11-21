// taskforge/Data/Models/DTO/PublicUserProfileDto.cs
using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO
{
    public sealed class PublicUserProfileDto
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        public string? AvatarUrl { get; set; } // если у тебя есть колонка с авой

        public string? Bio { get; set; }
        public string? Location { get; set; }
        public string? Education { get; set; }
        public List<string> Skills { get; set; } = new();

        public string? Github { get; set; }
        public string? Telegram { get; set; }
        public string? Website { get; set; }

        public int Rank { get; set; }
        public int SolvedAssignments { get; set; }
        public int TotalAttempts { get; set; }
    }
}
