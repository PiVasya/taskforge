using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Data;

namespace TaskForge.Education.Api.Endpoints;

internal static partial class EducationApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            EducationDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var targetId = request.TargetUserId.Value;
            var sourceRows = await db.GroupMembers.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var targetGroups = await db.GroupMembers.AsNoTracking()
                .Where(x => x.UserId == targetId)
                .Select(x => x.GroupId)
                .ToListAsync(ct);
            var targetSet = targetGroups.ToHashSet();
            var moved = 0;
            var deduplicated = 0;
            foreach (var row in sourceRows)
            {
                if (targetSet.Add(row.GroupId))
                {
                    row.UserId = targetId;
                    moved++;
                }
                else
                {
                    db.GroupMembers.Remove(row);
                    deduplicated++;
                }
            }

            var courses = await db.Courses.Where(x => x.OwnerIdsJson.Contains(request.SourceUserId.ToString())).ToListAsync(ct);
            var updatedCourses = 0;
            foreach (var course in courses)
            {
                var ids = ParseGuidArray(course.OwnerIdsJson);
                if (!ids.Remove(request.SourceUserId)) continue;
                ids.Add(targetId);
                course.OwnerIdsJson = JsonSerializer.Serialize(ids.OrderBy(x => x));
                course.UpdatedAt = DateTimeOffset.UtcNow;
                updatedCourses++;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { request.OperationId, movedMemberships = moved, deduplicatedMemberships = deduplicated, updatedCourses });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            EducationDbContext db,
            CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var members = await db.GroupMembers.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.GroupMembers.RemoveRange(members);

            var courses = await db.Courses.Where(x => x.OwnerIdsJson.Contains(request.SourceUserId.ToString())).ToListAsync(ct);
            var updatedCourses = 0;
            foreach (var course in courses)
            {
                var ids = ParseGuidArray(course.OwnerIdsJson);
                if (!ids.Remove(request.SourceUserId)) continue;
                course.OwnerIdsJson = JsonSerializer.Serialize(ids.OrderBy(x => x));
                course.UpdatedAt = DateTimeOffset.UtcNow;
                updatedCourses++;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { request.OperationId, deletedMemberships = members.Count, updatedCourses });
        });

        return app;
    }

    private static HashSet<Guid> ParseGuidArray(string? json)
    {
        try { return (JsonSerializer.Deserialize<Guid[]>(json ?? "[]") ?? []).ToHashSet(); }
        catch { return []; }
    }
}
