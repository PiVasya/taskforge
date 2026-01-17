namespace taskforge.Services.Interfaces
{
    public interface ICourseAccessService
    {
        Task<bool> CanViewCourseAsync(Guid userId, string? role, Guid courseId);
        Task<bool> CanEditCourseAsync(Guid userId, string? role, Guid courseId);
        Task<IReadOnlyList<Guid>> GetAccessibleCourseIdsAsync(Guid userId, string? role);
    }
}
