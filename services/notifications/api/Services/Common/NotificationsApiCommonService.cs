using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

using TaskForge.Notifications.Api.Contracts;
using static TaskForge.Notifications.Api.Services.Mapping.NotificationsApiMappingService;

namespace TaskForge.Notifications.Api.Services.Common;

internal static class NotificationsApiCommonService
{
    internal static bool IsAdmin(HttpContext http, IConfiguration cfg)
    {
        var principal = http.User?.Identity?.IsAuthenticated == true ? http.User : TaskForgeRequestSecurity.ValidateUser(http, cfg);
        return principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
    }

    internal static IResult Forbidden(string message) => Microsoft.AspNetCore.Http.Results.Json(new { message, code = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

}
