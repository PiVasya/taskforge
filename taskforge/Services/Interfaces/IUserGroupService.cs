using taskforge.Data.Models.DTO.UserGroups;

namespace taskforge.Services.Interfaces
{
    public interface IUserGroupService
    {
        Task<IReadOnlyList<UserGroupDto>> GetAllAsync(bool includeInactive);
        Task<IReadOnlyList<UserGroupDto>> GetForUserAsync(Guid userId, bool includeInactive);
        Task<UserGroupDto?> GetByIdAsync(Guid groupId);
        Task<IReadOnlyList<Guid>> GetUserGroupIdsAsync(Guid userId);
        Task<Guid> CreateAsync(CreateUserGroupRequest request);
        Task UpdateAsync(Guid groupId, UpdateUserGroupRequest request);
        Task DeleteAsync(Guid groupId);

        Task AddMemberAsync(Guid groupId, Guid userId);
        Task RemoveMemberAsync(Guid groupId, Guid userId);
    }
}
