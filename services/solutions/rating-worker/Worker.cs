using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TaskForge.Solutions.RatingWorker;

public sealed class Worker(ILogger<Worker> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory) : BackgroundService
{
    private const string FullRebuildCheckpoint = "UserRatingsFullRebuild";
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
                if (await IsFullRebuildDueAsync(stoppingToken))
                {
                    var rebuilt = await RebuildAllRatingsAsync(stoppingToken);
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

    private async Task<bool> IsFullRebuildDueAsync(CancellationToken ct)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        const string sql = """
            SELECT "UpdatedAt"
            FROM "RatingProjectionCheckpoints"
            WHERE "ProjectionName" = @name;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("name", FullRebuildCheckpoint);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null || value is DBNull) return true;

        var last = value is DateTimeOffset dto ? dto : new DateTimeOffset((DateTime)value, TimeSpan.Zero);
        return DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(FullRebuildIntervalHours());
    }

    private async Task<int> ProcessDirtyUsersAsync(CancellationToken ct)
    {
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
            WHERE "UserId" = ANY(@userIds);
            """, connection))
        {
            cleanup.Parameters.AddWithValue("userIds", userIds.ToArray());
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        return userIds.Count;
    }

    private async Task<int> RebuildAllRatingsAsync(CancellationToken ct)
    {
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

        await using (var dirtyCleanup = new NpgsqlCommand("DELETE FROM \"RatingDirtyUsers\";", connection))
        {
            await dirtyCleanup.ExecuteNonQueryAsync(ct);
        }

        await using (var checkpoint = new NpgsqlCommand("""
            INSERT INTO "RatingProjectionCheckpoints" ("ProjectionName", "LastEventSequence", "UpdatedAt")
            VALUES (@name, 0, NOW())
            ON CONFLICT ("ProjectionName") DO UPDATE SET
                "UpdatedAt" = EXCLUDED."UpdatedAt";
            """, connection))
        {
            checkpoint.Parameters.AddWithValue("name", FullRebuildCheckpoint);
            await checkpoint.ExecuteNonQueryAsync(ct);
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

        var assignmentIds = codeRows.Where(x => x.Accepted).Select(x => x.AssignmentId)
            .Concat(imageRows.Where(x => x.Passed).Select(x => x.AssignmentId))
            .Distinct()
            .ToArray();
        var metadata = await LoadAssignmentRatingsAsync(assignmentIds, ct);

        foreach (var userId in ids)
        {
            var userCodeRows = codeRows.Where(x => x.UserId == userId).ToList();
            var userImageRows = imageRows.Where(x => x.UserId == userId).ToList();
            var userTaskRows = taskRows.Where(x => x.UserId == userId).ToList();

            var solved = new Dictionary<Guid, SolvedAssignment>();
            foreach (var row in userCodeRows.Where(x => x.Accepted))
            {
                if (!metadata.TryGetValue(row.AssignmentId, out var rating)) continue;
                AddSolved(solved, row.AssignmentId, rating, row.CreatedAt);
            }

            foreach (var row in userImageRows.Where(x => x.Passed))
            {
                if (!metadata.TryGetValue(row.AssignmentId, out var rating)) continue;
                AddSolved(solved, row.AssignmentId, rating, row.CreatedAt);
            }

            foreach (var row in userTaskRows)
            {
                AddSolved(solved, row.AssignmentId, Math.Max(1, row.Rating), row.SubmittedAt);
            }

            var attempts = userCodeRows.Count + userImageRows.Count + userTaskRows.Count;
            var acceptedAttempts = userCodeRows.Count(x => x.Accepted) + userImageRows.Count(x => x.Passed) + userTaskRows.Count;
            var rejectedAttempts = Math.Max(0, attempts - acceptedAttempts);
            var totalScore = solved.Values.Sum(x => x.Rating);
            var solvedCount = solved.Count;
            var lastAcceptedAt = solved.Values.Select(x => (DateTimeOffset?)x.LastSubmittedAt).Max();

            if (attempts == 0 && solvedCount == 0)
            {
                if (deleteMissingRatings) await DeleteUserRatingAsync(connection, userId, ct);
                continue;
            }

            await UpsertUserRatingAsync(connection, userId, totalScore, solvedCount, attempts, acceptedAttempts, rejectedAttempts, lastAcceptedAt, ct);
        }
    }

    private static void AddSolved(IDictionary<Guid, SolvedAssignment> solved, Guid assignmentId, int rating, DateTimeOffset submittedAt)
    {
        if (assignmentId == Guid.Empty) return;
        rating = Math.Max(1, rating);
        if (solved.TryGetValue(assignmentId, out var existing))
        {
            solved[assignmentId] = new SolvedAssignment(Math.Max(existing.Rating, rating), existing.LastSubmittedAt > submittedAt ? existing.LastSubmittedAt : submittedAt);
        }
        else
        {
            solved[assignmentId] = new SolvedAssignment(rating, submittedAt);
        }
    }

    private static async Task<List<CodeActivityRow>> LoadCodeRowsAsync(NpgsqlConnection connection, Guid[] userIds, CancellationToken ct)
    {
        var rows = new List<CodeActivityRow>();
        await using var cmd = new NpgsqlCommand("""
            SELECT "UserId", "AssignmentId", "Status", "CreatedAt"
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
                string.Equals(reader.GetString(2), "Accepted", StringComparison.OrdinalIgnoreCase),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return rows;
    }

    private static async Task<List<ImageActivityRow>> LoadImageRowsAsync(NpgsqlConnection connection, Guid[] userIds, CancellationToken ct)
    {
        var rows = new List<ImageActivityRow>();
        await using var cmd = new NpgsqlCommand("""
            SELECT "UserId", "AssignmentId", "Passed", "CreatedAt"
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
                reader.GetBoolean(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return rows;
    }

    private async Task<List<TaskActivityRow>> LoadTaskRowsAsync(Guid[]? userIds, CancellationToken ct)
    {
        var baseUrl = ServiceUrl("TasksApi", "http://tasks-api:8080");
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/activity/leaderboard")
        {
            Content = JsonContent.Create(new ActivityLeaderboardRequest(null, null, userIds, null), options: JsonOptions)
        };
        AddInternalKey(msg);
        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(msg, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<TaskActivityRow>>(JsonOptions, ct) ?? [];
    }

    private async Task<Dictionary<Guid, int>> LoadAssignmentRatingsAsync(Guid[] assignmentIds, CancellationToken ct)
    {
        var ids = assignmentIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, int>();

        var baseUrl = ServiceUrl("TasksApi", "http://tasks-api:8080");
        var client = httpClientFactory.CreateClient();
        var rows = new List<AssignmentSummary>();
        foreach (var chunk in ids.Chunk(2000))
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/assignments/summaries")
            {
                Content = JsonContent.Create(new AssignmentIdsRequest(chunk), options: JsonOptions)
            };
            AddInternalKey(msg);
            using var response = await client.SendAsync(msg, ct);
            response.EnsureSuccessStatusCode();
            rows.AddRange(await response.Content.ReadFromJsonAsync<List<AssignmentSummary>>(JsonOptions, ct) ?? []);
        }

        return rows
            .Select(x => new { Id = x.AssignmentId == Guid.Empty ? x.Id : x.AssignmentId, x.Rating })
            .Where(x => x.Id != Guid.Empty)
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(x => x.Rating)));
    }

    private async Task UpsertUserRatingAsync(NpgsqlConnection connection, Guid userId, int totalScore, int solvedCount, int attemptsCount, int acceptedCount, int rejectedCount, DateTimeOffset? lastAcceptedAt, CancellationToken ct)
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
            """, connection);
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

    private static async Task DeleteUserRatingAsync(NpgsqlConnection connection, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM \"UserRatings\" WHERE \"UserId\" = @userId;", connection);
        cmd.Parameters.AddWithValue("userId", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string ServiceUrl(string name, string fallback)
        => (configuration[$"Services:{name}"] ?? configuration[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    private void AddInternalKey(HttpRequestMessage msg)
    {
        var key = configuration["InternalApi:Key"] ?? configuration["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    private sealed record CodeActivityRow(Guid UserId, Guid AssignmentId, bool Accepted, DateTimeOffset CreatedAt);
    private sealed record ImageActivityRow(Guid UserId, Guid AssignmentId, bool Passed, DateTimeOffset CreatedAt);
    private sealed record SolvedAssignment(int Rating, DateTimeOffset LastSubmittedAt);
    private sealed record ActivityLeaderboardRequest(Guid? CourseId, int? Days, Guid[]? UserIds, Guid[]? CourseIds = null);
    private sealed record AssignmentIdsRequest(Guid[]? AssignmentIds);

    private sealed class TaskActivityRow
    {
        public Guid UserId { get; set; }
        public Guid AssignmentId { get; set; }
        public int Rating { get; set; } = 1;
        public DateTimeOffset SubmittedAt { get; set; }
        public string? Kind { get; set; }
    }

    private sealed class AssignmentSummary
    {
        public Guid Id { get; set; }
        public Guid AssignmentId { get; set; }
        public int Rating { get; set; } = 1;
    }
}
