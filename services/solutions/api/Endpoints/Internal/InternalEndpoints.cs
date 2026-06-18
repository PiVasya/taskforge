using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/solutions/submissions/{submissionId:guid}/verdict", async (Guid submissionId, SolutionVerdictRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var sub = await db.Submissions.FirstOrDefaultAsync(x => x.Id == submissionId, ct);
            if (sub == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });

            var previous = sub.Status;
            var isTerminalBefore = IsTerminalVerdict(previous);
            sub.Status = CleanVerdict(request.Verdict);
            sub.Score = System.Math.Clamp(request.Score, 0, 100);
            sub.ResultJson = request.Result.HasValue
                ? request.Result.Value.GetRawText()
                : JsonSerializer.Serialize(new { verdict = sub.Status, score = sub.Score, message = request.Message }, JsonOptions());

            if (!isTerminalBefore && sub.UserId.HasValue && IsTerminalVerdict(sub.Status))
            {
                await AddRating(db, sub.UserId.Value, sub.Score, accepted: string.Equals(sub.Status, "Accepted", StringComparison.OrdinalIgnoreCase));
            }

            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(sub, includeSensitiveResult: true));
        });

        app.MapPost("/api/internal/users/{userId:guid}/solved-assignments", async (Guid userId, SolvedAssignmentsRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var ids = (request.AssignmentIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(2000)
                .ToArray();

            if (ids.Length == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new SolvedAssignmentsResponse(userId, Array.Empty<Guid>()));
            }

            var codeSolved = await db.Submissions.AsNoTracking()
                .Where(x => x.UserId == userId && ids.Contains(x.AssignmentId) && x.Status == "Accepted")
                .Select(x => x.AssignmentId)
                .ToListAsync(ct);

            var imageSolved = await db.ImageSolutions.AsNoTracking()
                .Where(x => x.UserId == userId && ids.Contains(x.AssignmentId) && x.Passed)
                .Select(x => x.AssignmentId)
                .ToListAsync(ct);

            var solved = codeSolved.Concat(imageSolved).Distinct().ToArray();
            return Microsoft.AspNetCore.Http.Results.Ok(new SolvedAssignmentsResponse(userId, solved));
        });

        app.MapGet("/api/internal/users/{userId:guid}/activity-summary", async (Guid userId, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var codeAttempts = await db.Submissions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
            var imageAttempts = await db.ImageSolutions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
            var solvedIds = codeAttempts.Where(x => x.Status == "Accepted").Select(x => x.AssignmentId)
                .Concat(imageAttempts.Where(x => x.Passed).Select(x => x.AssignmentId))
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            var metadata = await LoadAssignmentMetadataAsync(solvedIds, cfg, httpFactory, ct);
            var score = solvedIds.Sum(id => MetadataRating(metadata, id));
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                solvedAssignments = solvedIds.Length,
                totalAttempts = codeAttempts.Count + imageAttempts.Count,
                codeSolutions = codeAttempts.Count,
                imageSolutions = imageAttempts.Count,
                testAttempts = 0,
                mathAttempts = 0,
                score,
                rating = score
            });
        });

        return app;
    }
}
