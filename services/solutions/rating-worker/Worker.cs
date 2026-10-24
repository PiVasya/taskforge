using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TaskForge.Solutions.RatingWorker;

public sealed class Worker(ILogger<Worker> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory) : BackgroundService
{
    private const string FullRebuildCheckpoint = "UserRatingsFullRebuild";
    private const string FullRebuildRequestCheckpoint = "UserRatingsFullRebuildRequested";
    internal const string ActivitiesReadyCheckpoint = "RatingAcceptedActivitiesV1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Rating worker started. Dirty users are recalculated periodically; full rebuild runs as a safety net.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var plan = await GetFullRebuildPlanAsync(stoppingToken);
                if (plan.Due)
                {
                    var rebuilt = await RebuildAllRatingsAsync(plan.RequestGeneration, stoppingToken);
                    logger.LogInformation("Rating full rebuild completed. Affected users: {Count}.", rebuilt);
                }
                else
                {
                    var updated = await ProcessDirtyUsersAsync(stoppingToken);
                    if (updated > 0)
                    {
                        logger.LogInformation("Dirty rating users recalculated. Affected users: {Count}.", updated);
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(DirtyIntervalMinutes()), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rating projection update failed.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private int DirtyIntervalMinutes()
        => Math.Clamp(configuration.GetValue("Rating:DirtyIntervalMinutes", configuration.GetValue("Rating:RebuildIntervalMinutes", 10)), 1, 120);

    private int FullRebuildIntervalHours()
        => Math.Clamp(configuration.GetValue("Rating:FullRebuildIntervalHours", 24), 1, 24 * 30);

    private int BatchSize()
        => Math.Clamp(configuration.GetValue("Rating:DirtyBatchSize", 500), 50, 5000);

    private string ConnectionString()
        => configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

    private async Task<(bool Due, long RequestGeneration)> GetFullRebuildPlanAsync(CancellationToken ct)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand("""
            SELECT "ProjectionName", "LastEventSequence", "UpdatedAt"
            FROM "RatingProjectionCheckpoints"
            WHERE "ProjectionName" IN (@completedName, @requestedName, @activitiesName);
            """, connection);
        cmd.Parameters.AddWithValue("completedName", FullRebuildCheckpoint);
        cmd.Parameters.AddWithValue("requestedName", FullRebuildRequestCheckpoint);
        cmd.Parameters.AddWithValue("activitiesName", ActivitiesReadyCheckpoint);
        var activitiesReady = false;
        long completedGeneration = 0, requestGeneration = 0;
        DateTimeOffset? completedAt = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (name == FullRebuildCheckpoint)
                {
                    completedGeneration = reader.GetInt64(1);
                    completedAt = reader.GetFieldValue<DateTimeOffset>(2);
                }
                else if (name == FullRebuildRequestCheckpoint)
                {
                    requestGeneration = reader.GetInt64(1);
                }
                else if (name == ActivitiesReadyCheckpoint)
                {
                    activitiesReady = reader.GetInt64(1) == 1;
                }
            }
        }
        var due = ShouldRebuild(completedAt, completedGeneration, requestGeneration,
            DateTimeOffset.UtcNow, TimeSpan.FromHours(FullRebuildIntervalHours()));
        return (due || !activitiesReady, requestGeneration);
    }

    internal static bool ShouldRebuild(DateTimeOffset? completedAt, long completedGeneration, long requestedGeneration,
        DateTimeOffset now, TimeSpan interval)
        => completedAt is null || requestedGeneration > completedGeneration || now - completedAt.Value >= interval;

    private async Task<int> ProcessDirtyUsersAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var batchSize = BatchSize();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var userIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT "UserId"
            FROM "RatingDirtyUsers"
            ORDER BY "MarkedAtUtc"
            LIMIT @take;
            """, connection))
        {
            cmd.Parameters.AddWithValue("take", batchSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                userIds.Add(reader.GetGuid(0));
            }
        }

        if (userIds.Count == 0) return 0;

        await RecalculateUsersAsync(connection, userIds, deleteMissingRatings: true, ct);

        await using (var cleanup = new NpgsqlCommand("""
            DELETE FROM "RatingDirtyUsers"
            WHERE "UserId" = ANY(@userIds) AND "MarkedAtUtc" <= @startedAt;
            """, connection))
        {
            cleanup.Parameters.AddWithValue("userIds", userIds.ToArray());
            cleanup.Parameters.AddWithValue("startedAt", startedAt);
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        return userIds.Count;
    }

    private async Task<int> RebuildAllRatingsAsync(long requestGeneration, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var taskRows = await LoadTaskRowsAsync(null, ct);
        var userIds = new HashSet<Guid>(taskRows.Select(x => x.UserId).Where(x => x != Guid.Empty));

        await using (var cmd = new NpgsqlCommand("""
            SELECT DISTINCT "UserId"
            FROM "SolutionSubmissions"
            WHERE "UserId" IS NOT NULL
            UNION
            SELECT DISTINCT "UserId"
            FROM "UserImageTaskSolutions";
            """, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                userIds.Add(reader.GetGuid(0));
            }
        }

        var ids = userIds.ToArray();
        foreach (var chunk in ids.Chunk(BatchSize()))
        {
            var chunkTaskRows = taskRows.Where(x => chunk.Contains(x.UserId)).ToList();
            await RecalculateUsersAsync(connection, chunk, deleteMissingRatings: true, ct, preloadedTaskRows: chunkTaskRows);
        }

        await using (var cleanup = new NpgsqlCommand(ids.Length == 0
            ? "DELETE FROM \"UserRatings\";"
            : "DELETE FROM \"UserRatings\" WHERE NOT (\"UserId\" = ANY(@userIds));", connection))
        {
            if (ids.Length > 0) cleanup.Parameters.AddWithValue("userIds", ids);
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        await using (var orphanCleanup = new NpgsqlCommand(ids.Length == 0
            ? "DELETE FROM \"RatingAcceptedActivities\";"
            : "DELETE FROM \"RatingAcceptedActivities\" WHERE NOT (\"UserId\" = ANY(@userIds));", connection))
        {
            if (ids.Length > 0) orphanCleanup.Parameters.AddWithValue("userIds", ids);
            await orphanCleanup.ExecuteNonQueryAsync(ct);
        }

        await using (var dirtyCleanup = new NpgsqlCommand("DELETE FROM \"RatingDirtyUsers\" WHERE \"MarkedAtUtc\" <= @startedAt;", connection))
        {
            dirtyCleanup.Parameters.AddWithValue("startedAt", startedAt);
            await dirtyCleanup.ExecuteNonQueryAsync(ct);
        }

        await using (var checkpoint = new NpgsqlCommand("""
            INSERT INTO "RatingProjectionCheckpoints" ("ProjectionName", "LastEventSequence", "UpdatedAt")
            VALUES (@name, @requestGeneration, NOW())
            ON CONFLICT ("ProjectionName") DO UPDATE SET
                "LastEventSequence" = EXCLUDED."LastEventSequence",
                "UpdatedAt" = EXCLUDED."UpdatedAt";
            """, connection))
        {
            checkpoint.Parameters.AddWithValue("name", FullRebuildCheckpoint);
            checkpoint.Parameters.AddWithValue("requestGeneration", requestGeneration);
            await checkpoint.ExecuteNonQueryAsync(ct);
        }

        await using (var ready = new NpgsqlCommand("""
            INSERT INTO "RatingProjectionCheckpoints" ("ProjectionName", "LastEventSequence", "UpdatedAt")
            VALUES (@name, 1, NOW())
            ON CONFLICT ("ProjectionName") DO UPDATE SET
                "LastEventSequence" = 1, "UpdatedAt" = NOW();
            """, connection))
        {
            ready.Parameters.AddWithValue("name", ActivitiesReadyCheckpoint);
            await ready.ExecuteNonQueryAsync(ct);
        }

        return ids.Length;
    }

    private async Task RecalculateUsersAsync(NpgsqlConnection connection, IReadOnlyCollection<Guid> userIds, bool deleteMissingRatings, CancellationToken ct, IReadOnlyList<TaskActivityRow>? preloadedTaskRows = null)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return;

        var codeRows = await LoadCodeRowsAsync(connection, ids, ct);
        var imageRows = await LoadImageRowsAsync(connection, ids, ct);
        var taskRows = preloadedTaskRows ?? await LoadTaskRowsAsync(ids, ct);
        if (taskRows.Any(x => x.AttemptId == Guid.Empty || x.CourseId == Guid.Empty))
            throw new InvalidOperationException("Rating task activity is missing attemptId/courseId; update tasks-api before rating-worker.");

        var assignmentIds = codeRows.Where(x => x.Accepted).Select(x => x.AssignmentId)
            .Concat(imageRows.Where(x => x.Passed).Select(x => x.AssignmentId))
            .Distinct().ToArray();
        var metadata = await LoadAssignmentRatingsAsync(assignmentIds, ct);
        var codeByUser = codeRows.ToLookup(x => x.UserId);
        var imageByUser = imageRows.ToLookup(x => x.UserId);
        var taskByUser = taskRows.ToLookup(x => x.UserId);
        var accepted = new List<RatingAcceptedRow>();
        var summaries = new List<UserRatingSummary>(ids.Length);

        foreach (var userId in ids)
        {
            var userCodeRows = codeByUser[userId].ToArray();
            var userImageRows = imageByUser[userId].ToArray();
            var userTaskRows = taskByUser[userId].ToArray();
            var ownAccepted = new List<RatingAcceptedRow>();

            foreach (var row in userCodeRows.Where(x => x.Accepted))
            {
                if (!metadata.TryGetValue(row.AssignmentId, out var assignment)) continue;
                ownAccepted.Add(new RatingAcceptedRow("code", row.Id, userId, row.AssignmentId, assignment.CourseId, assignment.Rating, row.CreatedAt));
            }
            foreach (var row in userImageRows.Where(x => x.Passed))
            {
                if (!metadata.TryGetValue(row.AssignmentId, out var assignment)) continue;
                ownAccepted.Add(new RatingAcceptedRow("image", row.Id, userId, row.AssignmentId, assignment.CourseId, assignment.Rating, row.CreatedAt));
            }
            foreach (var row in userTaskRows)
            {
                ownAccepted.Add(new RatingAcceptedRow("task", row.AttemptId, userId, row.AssignmentId, row.CourseId, Math.Max(1, row.Rating), row.SubmittedAt));
            }
            accepted.AddRange(ownAccepted);
            var score = SummarizeAccepted(ownAccepted);
            var attempts = userCodeRows.Length + userImageRows.Length + userTaskRows.Length;
            var acceptedCount = userCodeRows.Count(x => x.Accepted) + userImageRows.Count(x => x.Passed) + userTaskRows.Length;
            summaries.Add(new UserRatingSummary(userId, score.TotalScore, score.SolvedCount, attempts, acceptedCount,
                Math.Max(0, attempts - acceptedCount), score.LastAcceptedAt));
        }

        // The stored global total and the accepted-activity detail must become
        // visible together; readers never see half-updated per-user projections.
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var clear = new NpgsqlCommand("DELETE FROM \"RatingAcceptedActivities\" WHERE \"UserId\" = ANY(@userIds);", connection, transaction))
        {
            clear.Parameters.AddWithValue("userIds", ids);
            await clear.ExecuteNonQueryAsync(ct);
        }
        await InsertAcceptedActivitiesAsync(connection, transaction, accepted, ct);
        foreach (var row in summaries)
        {
            if (row.AttemptsCount == 0 && row.SolvedCount == 0)
            {
                if (deleteMissingRatings) await DeleteUserRatingAsync(connection, transaction, row.UserId, ct);
                continue;
            }
            await UpsertUserRatingAsync(connection, transaction, row.UserId, row.TotalScore, row.SolvedCount,
                row.AttemptsCount, row.AcceptedCount, row.RejectedCount, row.LastAcceptedAt, ct);
        }
        await transaction.CommitAsync(ct);
    }

    internal static RatingScoreSummary SummarizeAccepted(IEnumerable<RatingAcceptedRow> rows)
    {
        var uniqueAssignments = rows.Where(x => x.AssignmentId != Guid.Empty)
            .GroupBy(x => x.AssignmentId)
            .Select(g => new { Rating = g.Max(x => Math.Max(1, x.Rating)), Last = g.Max(x => x.SubmittedAt) })
            .ToArray();
        return new RatingScoreSummary(
            uniqueAssignments.Sum(x => x.Rating),
            uniqueAssignments.Length,
            uniqueAssignments.Length == 0 ? null : uniqueAssignments.Max(x => (DateTimeOffset?)x.Last));
    }

    private static async Task InsertAcceptedActivitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<RatingAcceptedRow> rows, CancellationToken ct)
    {
        foreach (var chunk in rows.Chunk(1000))
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "RatingAcceptedActivities"
                    ("SourceKind", "SourceId", "UserId", "AssignmentId", "CourseId", "Rating", "SubmittedAt")
                SELECT x."SourceKind", x."SourceId", x."UserId", x."AssignmentId", x."CourseId", x."Rating", x."SubmittedAt"
                FROM unnest(@sourceKinds::text[], @sourceIds::uuid[], @userIds::uuid[],
                            @assignmentIds::uuid[], @courseIds::uuid[], @ratings::integer[],
                            @submittedAts::timestamptz[]) AS x
                    ("SourceKind", "SourceId", "UserId", "AssignmentId", "CourseId", "Rating", "SubmittedAt");
                """, connection, transaction);
            cmd.Parameters.AddWithValue("sourceKinds", chunk.Select(x => x.SourceKind).ToArray());
            cmd.Parameters.AddWithValue("sourceIds", chunk.Select(x => x.SourceId).ToArray());
            cmd.Parameters.AddWithValue("userIds", chunk.Select(x => x.UserId).ToArray());
            cmd.Parameters.AddWithValue("assignmentIds", chunk.Select(x => x.AssignmentId).ToArray());
            cmd.Parameters.AddWithValue("courseIds", chunk.Select(x => x.CourseId).ToArray());
            cmd.Parameters.AddWithValue("ratings", chunk.Select(x => x.Rating).ToArray());
            cmd.Parameters.Add("submittedAts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz).Value = chunk.Select(x => x.SubmittedAt).ToArray();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<List<CodeActivityRow>> LoadCodeRowsAsync(NpgsqlConnection connection, Guid[] userIds, CancellationToken ct)
    {
        var rows = new List<CodeActivityRow>();
        await using var cmd = new NpgsqlCommand("""
            SELECT "Id", "UserId", "AssignmentId", "Status", "CreatedAt"
            FROM "SolutionSubmissions"
            WHERE "UserId" = ANY(@userIds)
              AND "Status" IN ('Accepted', 'Rejected', 'CompileError');
            """, connection);
        cmd.Parameters.AddWithValue("userIds", userIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CodeActivityRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                string.Equals(reader.GetString(3), "Accepted", StringComparison.OrdinalIgnoreCase),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return rows;
    }

    private static async Task<List<ImageActivityRow>> LoadImageRowsAsync(NpgsqlConnection connection, Guid[] userIds, CancellationToken ct)
    {
        var rows = new List<ImageActivityRow>();
        await using var cmd = new NpgsqlCommand("""
            SELECT "Id", "UserId", "AssignmentId", "Passed", "CreatedAt"
            FROM "UserImageTaskSolutions"
            WHERE "UserId" = ANY(@userIds);
            """, connection);
        cmd.Parameters.AddWithValue("userIds", userIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ImageActivityRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetBoolean(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return rows;
    }

    private async Task<List<TaskActivityRow>> LoadTaskRowsAsync(Guid[]? userIds, CancellationToken ct)
    {
        var baseUrl = ServiceUrl("TasksApi", "http://tasks-api:8080");
        var client = httpClientFactory.CreateClient();
        using var response = await SendTasksApiAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/activity/leaderboard")
            {
                Content = JsonContent.Create(new ActivityLeaderboardRequest(null, null, userIds, null), options: JsonOptions)
            },
            "activity-leaderboard",
            ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<TaskActivityRow>>(JsonOptions, ct) ?? [];
    }

    private async Task<Dictionary<Guid, AssignmentRating>> LoadAssignmentRatingsAsync(Guid[] assignmentIds, CancellationToken ct)
    {
        var ids = assignmentIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, AssignmentRating>();

        var baseUrl = ServiceUrl("TasksApi", "http://tasks-api:8080");
        var client = httpClientFactory.CreateClient();
        var rows = new List<AssignmentSummary>();
        foreach (var chunk in ids.Chunk(2000))
        {
            using var response = await SendTasksApiAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/assignments/summaries")
                {
                    Content = JsonContent.Create(new AssignmentIdsRequest(chunk), options: JsonOptions)
                },
                "assignment-summaries",
                ct);
            response.EnsureSuccessStatusCode();
            rows.AddRange(await response.Content.ReadFromJsonAsync<List<AssignmentSummary>>(JsonOptions, ct) ?? []);
        }

        return rows
            .Select(x => new { Id = x.AssignmentId == Guid.Empty ? x.Id : x.AssignmentId, x.CourseId, x.Rating })
            .Where(x => x.Id != Guid.Empty && x.CourseId != Guid.Empty)
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => new AssignmentRating(g.First().CourseId, Math.Max(1, g.Max(x => x.Rating))));
    }

    private async Task UpsertUserRatingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, int totalScore, int solvedCount, int attemptsCount, int acceptedCount, int rejectedCount, DateTimeOffset? lastAcceptedAt, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "UserRatings" (
                "UserId", "TotalScore", "SolvedCount", "AttemptsCount",
                "AcceptedCount", "RejectedCount", "LastAcceptedAt", "UpdatedAt"
            )
            VALUES (@userId, @totalScore, @solvedCount, @attemptsCount, @acceptedCount, @rejectedCount, @lastAcceptedAt, NOW())
            ON CONFLICT ("UserId") DO UPDATE SET
                "TotalScore" = EXCLUDED."TotalScore",
                "SolvedCount" = EXCLUDED."SolvedCount",
                "AttemptsCount" = EXCLUDED."AttemptsCount",
                "AcceptedCount" = EXCLUDED."AcceptedCount",
                "RejectedCount" = EXCLUDED."RejectedCount",
                "LastAcceptedAt" = EXCLUDED."LastAcceptedAt",
                "UpdatedAt" = EXCLUDED."UpdatedAt";
            """, connection, transaction);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("totalScore", totalScore);
        cmd.Parameters.AddWithValue("solvedCount", solvedCount);
        cmd.Parameters.AddWithValue("attemptsCount", attemptsCount);
        cmd.Parameters.AddWithValue("acceptedCount", acceptedCount);
        cmd.Parameters.AddWithValue("rejectedCount", rejectedCount);
        var lastAcceptedParam = cmd.Parameters.Add("lastAcceptedAt", NpgsqlDbType.TimestampTz);
        lastAcceptedParam.Value = lastAcceptedAt.HasValue ? lastAcceptedAt.Value : DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteUserRatingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM \"UserRatings\" WHERE \"UserId\" = @userId;", connection, transaction);
        cmd.Parameters.AddWithValue("userId", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private Task<HttpResponseMessage> SendTasksApiAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken ct)
    {
        var attempts = Math.Clamp(configuration.GetValue("Rating:TasksApiRetryAttempts", TasksApiRetryPolicy.DefaultMaxAttempts), 1, 12);
        return TasksApiRetryPolicy.SendAsync(
            client,
            () =>
            {
                var request = requestFactory();
                AddInternalKey(request);
                return request;
            },
            logger,
            operation,
            ct,
            attempts);
    }

    private string ServiceUrl(string name, string fallback)
        => (configuration[$"Services:{name}"] ?? configuration[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    private void AddInternalKey(HttpRequestMessage msg)
    {
        var key = configuration["InternalApi:Key"] ?? configuration["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    private sealed record CodeActivityRow(Guid Id, Guid UserId, Guid AssignmentId, bool Accepted, DateTimeOffset CreatedAt);
    private sealed record ImageActivityRow(Guid Id, Guid UserId, Guid AssignmentId, bool Passed, DateTimeOffset CreatedAt);
    internal sealed record RatingAcceptedRow(string SourceKind, Guid SourceId, Guid UserId, Guid AssignmentId, Guid CourseId, int Rating, DateTimeOffset SubmittedAt);
    internal sealed record RatingScoreSummary(int TotalScore, int SolvedCount, DateTimeOffset? LastAcceptedAt);
    private sealed record UserRatingSummary(Guid UserId, int TotalScore, int SolvedCount, int AttemptsCount, int AcceptedCount, int RejectedCount, DateTimeOffset? LastAcceptedAt);
    private sealed record AssignmentRating(Guid CourseId, int Rating);
    private sealed record ActivityLeaderboardRequest(Guid? CourseId, int? Days, Guid[]? UserIds, Guid[]? CourseIds = null);
    private sealed record AssignmentIdsRequest(Guid[]? AssignmentIds);

    private sealed class TaskActivityRow
    {
        public Guid AttemptId { get; set; }
        public Guid CourseId { get; set; }
        public Guid UserId { get; set; }
        public Guid AssignmentId { get; set; }
        public int Rating { get; set; } = 1;
        public DateTimeOffset SubmittedAt { get; set; }
        public string? Kind { get; set; }
    }

    private sealed class AssignmentSummary
    {
        public Guid CourseId { get; set; }
        public Guid Id { get; set; }
        public Guid AssignmentId { get; set; }
        public int Rating { get; set; } = 1;
    }
}
