using Microsoft.AspNetCore.Http;
using System.Security.Claims;
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

        public string? GetRole()
        {
            // стандартный и “простой” варианты
            return _http.HttpContext?.User?.FindFirstValue(ClaimTypes.Role)
                   ?? _http.HttpContext?.User?.FindFirstValue("role");
        }

        public bool IsAdmin() => string.Equals(GetRole(), "Admin", StringComparison.OrdinalIgnoreCase);
    }
}
