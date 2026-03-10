namespace taskforge.Services.Interfaces
{
    public interface ICurrentUserService
    {
        Guid GetUserId();
        string? GetRole();
        string? GetPrimaryRole();
        IReadOnlyList<string> GetRoles();
        bool HasRole(string role);
        bool HasAnyRole(params string[] roles);
        bool IsAdmin();
        bool IsEditor();
        bool IsAdminOrEditor();
    }
}
