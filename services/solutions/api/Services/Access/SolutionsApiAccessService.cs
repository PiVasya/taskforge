using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Services.Access;

internal static class SolutionsApiAccessService
{
    internal static async Task<AssignmentAccessDto?> LoadAssignmentAccessAsync(Guid assignmentId, Guid userId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        return await GetInternalAsync<AssignmentAccessDto>(httpFactory, cfg, ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080"), $"/api/internal/assignments/{assignmentId:D}/access/{userId:D}", ct);
    }

    internal static async Task<CourseAccessDto?> LoadCourseAccessAsync(Guid courseId, Guid userId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        return await GetInternalAsync<CourseAccessDto>(httpFactory, cfg, ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/access/{userId:D}", ct);
    }

    internal static Guid? CurrentUserId(HttpContext http, IConfiguration cfg) => TaskForgeRequestSecurity.UserId(http, cfg);

    internal static bool IsAdmin(HttpContext http, IConfiguration cfg)
    {
        var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
        return principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
    }

    internal static bool IsEditor(HttpContext http, IConfiguration cfg)
    {
        var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
        return principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor");
    }

}
