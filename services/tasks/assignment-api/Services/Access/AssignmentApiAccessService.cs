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
    internal static async Task<bool> CanUserAccessAssignmentAsync(Assignment assignment, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        var editor = IsEditor(http, cfg);
        var userId = TaskForgeRequestSecurity.UserId(http, cfg);
        TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_BEGIN",
            ("user", userId),
            ("assignment", assignment.Id),
            ("course", assignment.CourseId),
            ("title", assignment.Title),
            ("assignmentVisible", assignment.IsVisible),
            ("editorBypass", editor));

        if (editor)
        {
            TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_END", ("user", userId), ("assignment", assignment.Id), ("allowed", true), ("reason", "editor-bypass"));
            return true;
        }
        if (!assignment.IsVisible)
        {
            TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_END", ("user", userId), ("assignment", assignment.Id), ("allowed", false), ("reason", "assignment-hidden"));
            return false;
        }
        if (!userId.HasValue)
        {
            TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_END", ("user", "anonymous"), ("assignment", assignment.Id), ("allowed", false), ("reason", "no-user"));
            return false;
        }

        var courseAccess = await LoadCourseAccessRowsAsync(
            new[] { assignment.CourseId },
            userId.Value,
            clients,
            cfg,
            ct);
        var courseCanView = courseAccess.Count > 0 && courseAccess[0].CanView;
        TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_COURSE",
            ("user", userId.Value),
            ("assignment", assignment.Id),
            ("course", assignment.CourseId),
            ("rows", courseAccess.Count),
            ("canView", courseCanView));
        if (!courseCanView)
        {
            TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_END", ("user", userId.Value), ("assignment", assignment.Id), ("allowed", false), ("reason", "course-denied"));
            return false;
        }

        var projection = http.RequestServices.GetService<CourseMapProjectionService>();
        if (projection is not null)
        {
            var cached = await projection.TryGetCachedAssignmentAccessAsync(
                assignment.CourseId,
                assignment.Id,
                userId.Value,
                bypassStudentVisibility: false,
                ct);
            TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_PROJECTION",
                ("user", userId.Value),
                ("assignment", assignment.Id),
                ("course", assignment.CourseId),
                ("cached", cached.HasValue ? cached.Value : "miss"),
                ("decision", cached == true ? "allow" : cached == false ? "authoritative-recheck" : "authoritative-no-cache"));
            if (cached == true)
            {
                TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_END", ("user", userId.Value), ("assignment", assignment.Id), ("allowed", true), ("reason", "projection-positive"));
                return true;
            }
        }

        var evaluation = await CourseMapProgressionService.LoadEvaluationAsync(assignment.CourseId, userId.Value, db, clients, cfg, ct);
        var allowed = evaluation?.VisibleAssignmentIds.Contains(assignment.Id) == true;
        TaskForgeDebugTrace.Map("ASSIGNMENT_ACCESS_END",
            ("user", userId.Value),
            ("assignment", assignment.Id),
            ("course", assignment.CourseId),
            ("allowed", allowed),
            ("reason", "authoritative-evaluation"),
            ("evaluationPresent", evaluation is not null),
            ("visibleAssignmentIds", TaskForgeDebugTrace.MapList(evaluation?.VisibleAssignmentIds)));
        return allowed;
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


    internal static async Task<List<CourseAccessDto>> LoadCourseAccessRowsAsync(IEnumerable<Guid> courseIds, Guid userId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct, bool bypassStudentVisibility = false)
    {
        var ids = courseIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
        if (ids.Length == 0 || userId == Guid.Empty) return new List<CourseAccessDto>();

        return await PostInternalAsync<List<CourseAccessDto>>(
            clients,
            cfg,
            ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
            "/api/internal/courses/access",
            new { userId, courseIds = ids, bypassStudentVisibility, includeProgressionRules = false },
            ct)
            ?? new List<CourseAccessDto>();
    }

    internal static async Task<HashSet<Guid>> LoadAccessibleCourseIdsAsync(IEnumerable<Guid> courseIds, Guid userId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct, bool bypassStudentVisibility = false)
    {
        var ids = courseIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        var result = new HashSet<Guid>();
        foreach (var batch in ids.Chunk(2000))
        {
            var rows = await LoadCourseAccessRowsAsync(batch, userId, clients, cfg, ct, bypassStudentVisibility);
            foreach (var row in rows)
            {
                if (row.CanView) result.Add(row.CourseId);
            }
        }
        return result;
    }

    internal static bool IsEditor(HttpContext http, IConfiguration cfg)
    {
        var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
        return principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor");
    }

}
