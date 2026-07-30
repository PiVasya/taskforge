using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;

namespace TaskForge.Execution.Api.Endpoints;

internal static partial class ExecutionApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            ExecutionDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });
            var rows = await db.ExecutionJobs.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in rows) row.UserId = request.TargetUserId.Value;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.OperationId, movedExecutionJobs = rows.Count });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            ExecutionDbContext db,
            CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var jobs = await db.ExecutionJobs.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var jobIds = jobs.Select(x => x.Id).ToArray();
            var results = await db.ExecutionResults.Where(x => jobIds.Contains(x.JobId)).ToListAsync(ct);
            db.ExecutionResults.RemoveRange(results);
            db.ExecutionJobs.RemoveRange(jobs);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { request.OperationId, deletedExecutionJobs = jobs.Count, deletedExecutionResults = results.Count });
        });

        return app;
    }
}
