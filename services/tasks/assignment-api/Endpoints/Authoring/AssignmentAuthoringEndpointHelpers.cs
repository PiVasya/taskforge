using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static async Task<int> NextAssignmentSortAsync(TasksDbContext db, Guid courseId, CancellationToken ct)
        => (await db.Assignments.Where(x => x.CourseId == courseId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1) + 1;

    private static async Task<(Assignment? Assignment, IResult? Error)> RequireTypedAssignmentForEditAsync(
        Guid assignmentId,
        string expectedType,
        HttpContext http,
        IConfiguration cfg,
        TasksDbContext db,
        IHttpClientFactory clients,
        CancellationToken ct,
        bool tracked = false)
    {
        if (!IsEditor(http, cfg))
            return (null, Results.Json(new { message = "Для редактирования нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden));

        var query = tracked ? db.Assignments.AsQueryable() : db.Assignments.AsNoTracking();
        var assignment = await query.FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (assignment is null)
            return (null, Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" }));
        if (!await CanUserEditCourseAsync(assignment.CourseId, http, cfg, clients, ct))
            return (null, CourseEditForbidden());

        if (!TryNormalizeAssignmentType(assignment.Type, out var actualType) || !string.Equals(actualType, expectedType, StringComparison.Ordinal))
        {
            var displayedType = string.IsNullOrWhiteSpace(assignment.Type) ? "<empty>" : assignment.Type.Trim();
            return (null, Results.Conflict(new
            {
                message = $"Ожидался тип задания {expectedType}, фактический тип {displayedType}.",
                code = "ASSIGNMENT_TYPE_MISMATCH"
            }));
        }

        return (assignment, null);
    }

    private static async Task MarkTypedAssignmentChangeAsync(
        Assignment assignment,
        int oldRating,
        bool oldVisible,
        IHttpClientFactory clients,
        IConfiguration cfg,
        TasksDbContext db,
        CancellationToken ct)
    {
        if (oldRating == assignment.Rating && oldVisible == assignment.IsVisible) return;
        var users = await db.Attempts.AsNoTracking()
            .Where(x => x.TaskAssignmentId == assignment.Id)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);
        await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, assignment.Id, users, "assignment-updated", ct);
    }
}
