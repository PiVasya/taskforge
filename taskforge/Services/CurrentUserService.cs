using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using taskforge.Constants;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _http;
        public CurrentUserService(IHttpContextAccessor http) => _http = http;

        public Guid GetUserId()
        {
            var sub = _http.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? _http.HttpContext?.User?.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var id))
                throw new InvalidOperationException("UserId is missing");

            return id;
        }

        public string? GetRole() => GetPrimaryRole();

        public string? GetPrimaryRole()
        {
            return _http.HttpContext?.User?.FindFirstValue(ClaimTypes.Role)
                   ?? _http.HttpContext?.User?.FindFirstValue("role");
        }

        public IReadOnlyList<string> GetRoles()
        {
            var user = _http.HttpContext?.User;
            if (user == null) return Array.Empty<string>();

            var roles = new List<string>();

            foreach (var claim in user.FindAll(ClaimTypes.Role))
                AddRolesFromRaw(roles, claim.Value);

            foreach (var claim in user.FindAll("role"))
                AddRolesFromRaw(roles, claim.Value);

            foreach (var claim in user.FindAll("roles"))
                AddRolesFromRaw(roles, claim.Value);

            return roles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool HasRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;
            return GetRoles().Contains(role, StringComparer.OrdinalIgnoreCase);
        }

        public bool HasAnyRole(params string[] roles)
        {
            if (roles == null || roles.Length == 0) return false;
            var current = GetRoles();
            return roles.Any(role => current.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        public bool IsAdmin() => HasRole(AppRoles.Admin);

        public bool IsEditor() => HasRole(AppRoles.Editor);

        public bool IsAdminOrEditor() => IsAdmin() || IsEditor();

        private static void AddRolesFromRaw(List<string> roles, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part)) roles.Add(part);
            }
        }
    }
}
