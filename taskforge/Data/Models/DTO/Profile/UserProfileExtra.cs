// taskforge/Data/Models/Profile/UserProfileExtra.cs
using System.Collections.Generic;

namespace taskforge.Data.Models.Profile
{
    public sealed class UserProfileExtra
    {
        public string? Bio { get; set; }
        public string? Location { get; set; }
        public string? Education { get; set; }

        public UserProfileLinks Links { get; set; } = new();
        public List<string> Skills { get; set; } = new();
        public bool ShowInLeaderboard { get; set; } = true;
    }

    public sealed class UserProfileLinks
    {
        public string? Github { get; set; }
        public string? Telegram { get; set; }
        public string? Website { get; set; }
    }
}
