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
    }
}
