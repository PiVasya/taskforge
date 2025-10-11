using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces;

public interface ISolutionAdminService
{
    Task<IList<SolutionListItemDto>> GetByUserAsync(Guid userId, Guid? courseId, Guid? assignmentId, int skip, int take);
    Task<SolutionDetailsDto?> GetDetailsAsync(Guid solutionId);
    Task<IList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid? courseId, int? days, int top);
    Task<IList<UserShortDto>> SearchUsersAsync(string query, int take);
}

public sealed class UserShortDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}
