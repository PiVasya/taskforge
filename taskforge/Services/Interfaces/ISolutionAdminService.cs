using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ISolutionAdminService
    {
        Task<IList<UserShortDto>> SearchUsersAsync(string query, int take);

        Task<IList<SolutionListItemDto>> GetByUserAsync(
            Guid userId,
            Guid? courseId,
            Guid? assignmentId,
            int skip,
            int take
        );

        Task<IList<SolutionListItemDto>> GetAllByUserAsync(
            Guid userId,
            Guid? courseId,
            Guid? assignmentId,
            int? days
        );

        /// <summary>Удаление пользователя целиком (для админа).</summary>
        Task DeleteUserAsync(Guid userId);

        Task DeleteUserSolutionsAsync(Guid userId, Guid? courseId, Guid? assignmentId);

        Task<SolutionDetailsDto?> GetDetailsAsync(Guid solutionId);

        // bulk для ускоренной загрузки деталей
        Task<List<SolutionDetailsDto>> GetDetailsBulkAsync(IEnumerable<Guid> ids);

        Task<IList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid? courseId, int? days, int top);
    }
}
