namespace taskforge.Services.Interfaces
{
    public interface IFeatureRoleService
    {
        Task EnsureDefaultRolesAsync(CancellationToken ct = default);
        Task<IReadOnlyList<string>> GetRoleCodesForUserAsync(Guid userId, CancellationToken ct = default);
        Task AssignRoleAsync(Guid userId, string roleCode, Guid? assignedByUserId = null, CancellationToken ct = default);
        Task RemoveRoleAsync(Guid userId, string roleCode, CancellationToken ct = default);
        Task SyncMinecraftLinkedUsersAsync(CancellationToken ct = default);
    }
}
