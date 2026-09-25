using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Serialization.EducationApiSerializationService;

namespace TaskForge.Education.Api.Services.Access;

internal static class EducationApiAccessService
{
    internal static async Task<EducationAccessContext> ResolveAccessContext(HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct)
    {
        var principal = http.User?.Identity?.IsAuthenticated == true ? http.User : TaskForgeRequestSecurity.ValidateUser(http, cfg);
        var isAuthor = principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor", "SuperAdmin");
        var isSuperAdmin = principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "SuperAdmin");
        var userId = TaskForgeRequestSecurity.UserId(http, cfg);
        var groupIds = userId.HasValue
            ? await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId.Value).Select(x => x.GroupId).ToListAsync(ct)
            : new List<Guid>();

        var roleRank = isSuperAdmin ? 1000
            : principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin") ? 800
            : principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "Editor", "LearningEditor") ? 400
            : 0;

        var userRanks = new Dictionary<Guid, int>();
        if (isAuthor && userId.HasValue)
        {
            var ownerJsonRows = await db.Courses.AsNoTracking().Select(x => x.OwnerIdsJson).ToListAsync(ct);
            var ids = ownerJsonRows.SelectMany(DeserializeIds).Append(userId.Value).Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
            var levels = await LoadUserAccessLevelsAsync(ids, http.RequestServices.GetRequiredService<IHttpClientFactory>(), cfg, ct);
            userRanks = levels.ToDictionary(x => x.UserId, x => x.EffectiveRank);
            if (levels.FirstOrDefault(x => x.UserId == userId.Value) is { } actor)
            {
                roleRank = actor.EffectiveRank;
                isSuperAdmin = actor.IsSuperAdmin;
                isAuthor = actor.CanAuthorCourses;
            }
        }

        return new EducationAccessContext(userId, isAuthor, false, groupIds.ToHashSet(), roleRank, isSuperAdmin, userRanks);
    }

    internal static async Task<List<UserAccessLevelDto>> LoadUserAccessLevelsAsync(IEnumerable<Guid> userIds, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
        if (ids.Length == 0) return new List<UserAccessLevelDto>();
        try
        {
            var baseUrl = (cfg["Services:IdentityApi"] ?? cfg["ServiceUrls:IdentityApi"] ?? "http://identity-api:8080").TrimEnd('/');
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/users/access-levels")
            {
                Content = JsonContent.Create(new { userIds = ids })
            };
            var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
            if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
            using var response = await clients.CreateClient().SendAsync(msg, ct);
            if (!response.IsSuccessStatusCode) return new List<UserAccessLevelDto>();
            return await response.Content.ReadFromJsonAsync<List<UserAccessLevelDto>>(cancellationToken: ct) ?? new List<UserAccessLevelDto>();
        }
        catch
        {
            return new List<UserAccessLevelDto>();
        }
    }

    internal static EducationAccessContext BuildInternalAccess(Guid userId, IReadOnlyCollection<Guid> groupIds, IReadOnlyCollection<UserAccessLevelDto> levels, bool bypassStudentVisibility = false)
    {
        var actor = levels.FirstOrDefault(x => x.UserId == userId);
        var ranks = levels.ToDictionary(x => x.UserId, x => x.EffectiveRank);
        return new EducationAccessContext(
            userId,
            actor?.CanAuthorCourses == true,
            bypassStudentVisibility,
            groupIds.ToHashSet(),
            actor?.EffectiveRank ?? 0,
            actor?.IsSuperAdmin == true,
            ranks);
    }
}
