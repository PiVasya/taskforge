using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Runtime;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private sealed record RuntimeAssignmentLoad(Assignment? Assignment, bool IncludeSensitive, IResult? Error);

    private static async Task<RuntimeAssignmentLoad> RequireTypedAssignmentForRuntimeAsync(
        Guid assignmentId,
        string expectedType,
        HttpContext http,
        IConfiguration cfg,
        TasksDbContext db,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (assignment is null)
            return new(null, false, Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" }));

        var actualType = AssignmentTypedReadService.NormalizeStoredType(assignment);
        if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
            return new(null, false, Results.Conflict(new { message = "Маршрут не соответствует типу задания.", code = "ASSIGNMENT_TYPE_MISMATCH", expectedType, actualType }));

        var includeSensitive = await CanUserEditCourseAsync(assignment.CourseId, http, cfg, clients, ct);
        if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct))
            return new(null, false, Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" }));

        return new(assignment, includeSensitive, null);
    }

    private static async Task<bool> IsSolvedForCurrentUserAsync(
        Assignment assignment,
        HttpContext http,
        IConfiguration cfg,
        TasksDbContext db,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        var userId = TaskForgeRequestSecurity.UserId(http, cfg);
        if (!userId.HasValue) return false;
        var solved = await LoadSolvedAssignmentIdsAsync(userId.Value, new[] { assignment.Id }, db, clients, cfg, ct);
        return solved.Contains(assignment.Id);
    }
}
