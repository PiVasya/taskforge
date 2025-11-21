// taskforge/Services/Interfaces/ILeaderboardService.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ILeaderboardService
    {
        Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync();

        Task<PublicUserProfileDto?> GetPublicProfileAsync(Guid userId);
    }
}
