using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Access;

internal static class AssignmentApiAccessService
{
    internal static async Task<bool> CanUserAccessAssignmentAsync(Assignment assignment, HttpContext http, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct)
    {
        if (IsEditor(http, cfg)) return true;
        if (!assignment.IsVisible) return false;
        return await CanUserAccessCourseAsync(assignment.CourseId, http, cfg, clients, ct);
    }

    internal static async Task<bool> CanUserAccessCourseAsync(Guid courseId, HttpContext http, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct)
    {
        if (IsEditor(http, cfg)) return true;
        var userId = TaskForgeRequestSecurity.UserId(http, cfg);
        if (!userId.HasValue) return false;
        var access = await LoadCourseAccessAsync(courseId, userId.Value, clients, cfg, ct);
        return access?.CanView == true;
    }

    internal static async Task<CourseAccessDto?> LoadCourseAccessAsync(Guid courseId, Guid userId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        return await GetInternalAsync<CourseAccessDto>(clients, cfg, ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/access/{userId:D}", ct);
    }


    internal static async Task<HashSet<Guid>> LoadAccessibleCourseIdsAsync(IEnumerable<Guid> courseIds, Guid userId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var ids = courseIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
        if (ids.Length == 0 || userId == Guid.Empty) return new HashSet<Guid>();

        var rows = await PostInternalAsync<List<CourseAccessDto>>(
            clients,
            cfg,
            ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
            "/api/internal/courses/access",
            new { userId, courseIds = ids },
            ct);

        return rows?
            .Where(x => x.CanView)
            .Select(x => x.CourseId)
            .ToHashSet()
            ?? new HashSet<Guid>();
    }

    internal static bool IsEditor(HttpContext http, IConfiguration cfg)
    {
        var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
        return principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor");
    }

}
