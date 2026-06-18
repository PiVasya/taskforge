using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;
using static TaskForge.Education.Api.Services.Mapping.EducationApiMappingService;
using static TaskForge.Education.Api.Services.Serialization.EducationApiSerializationService;

namespace TaskForge.Education.Api.Services.Access;

internal static class EducationApiAccessService
{
    internal static async Task<EducationAccessContext> ResolveAccessContext(HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct)
    {
        var principal = http.User?.Identity?.IsAuthenticated == true ? http.User : TaskForgeRequestSecurity.ValidateUser(http, cfg);
        var isEditor = principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor");
        var userId = TaskForgeRequestSecurity.UserId(http, cfg);
        var groupIds = userId.HasValue
            ? await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId.Value).Select(x => x.GroupId).ToListAsync(ct)
            : new List<Guid>();
        return new EducationAccessContext(userId, isEditor, groupIds.ToHashSet());
    }

}
