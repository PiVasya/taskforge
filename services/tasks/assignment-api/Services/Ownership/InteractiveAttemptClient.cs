using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskForge.Tasks.Api.Domain;

namespace TaskForge.Tasks.Api.Services.Ownership;

// Tasks remains the *grader*, but Solutions is the only writer / owner of
// test and math attempts. Nothing here queries the Tasks database for attempts.
internal static class InteractiveAttemptClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed record AttemptSearch(
        string? Kind = null, Guid? UserId = null, Guid? AssignmentId = null,
        Guid[]? AssignmentIds = null, DateTimeOffset? Since = null,
        DateTimeOffset? Until = null, bool SubmittedOnly = false,
        bool PassedOnly = false, int Skip = 0, int Take = 500,
        bool NewestFirst = true);
    internal sealed record AttemptSearchResult(long Total, TaskAttempt[] Rows);
    internal sealed record AttemptStartResult(TaskAttempt Attempt, bool Created);
    internal sealed record DeleteResult(int Deleted, Guid[]? UserIds = null);

    private static HttpRequestMessage MakeRequest(IConfiguration cfg, HttpMethod method, string path, object? body = null)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ??
            cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Solutions attempt ownership requires an internal service credential.");
        var baseUrl = cfg["Services:SolutionsApi"] ?? cfg["ServiceUrls:SolutionsApi"] ?? "http://solutions-api:8080";
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + path, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Invalid Solutions API URL.");
        var msg = new HttpRequestMessage(method, uri);
        msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
        if (body is not null) msg.Content = JsonContent.Create(body, options: Json);
        return msg;
    }

    private static async Task<T?> SendAsync<T>(IHttpClientFactory factory, IConfiguration cfg, HttpMethod method,
        string path, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        using var msg = MakeRequest(cfg, method, path, body);
        using var response = await factory.CreateClient("interactive-attempt-owner").SendAsync(msg, ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return default;
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            string? code = null;
            try { code = JsonDocument.Parse(raw).RootElement.GetProperty("code").GetString(); }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            throw new InteractiveAttemptConflictException(code ?? "ATTEMPT_CONFLICT");
        }
        // Never silently assume an empty history on a failed owner request.
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new HttpRequestException("Solutions returned an empty attempt-owner response.");
    }

    internal static Task<TaskForge.Tasks.Api.Services.Results.PublicTaskStatistics?> GetPublicStatisticsAsync(
        IHttpClientFactory factory, IConfiguration cfg, Guid userId, CancellationToken ct) =>
        SendAsync<TaskForge.Tasks.Api.Services.Results.PublicTaskStatistics>(factory, cfg, HttpMethod.Get,
            $"/api/internal/task-attempts/users/{userId:D}/public-statistics", null, ct);

    internal static Task<TaskAttempt?> FindAsync(IHttpClientFactory factory, IConfiguration cfg, Guid id, CancellationToken ct) =>
        SendAsync<TaskAttempt>(factory, cfg, HttpMethod.Get, $"/api/internal/task-attempts/{id:D}", null, ct, true);

    internal static Task<TaskAttempt?> ActiveAsync(IHttpClientFactory factory, IConfiguration cfg,
        string kind, Guid userId, Guid assignmentId, CancellationToken ct) =>
        SendAsync<TaskAttempt>(factory, cfg, HttpMethod.Get,
            $"/api/internal/task-attempts/active?kind={Uri.EscapeDataString(kind)}&userId={userId:D}&assignmentId={assignmentId:D}", null, ct, true);

    internal static async Task<AttemptSearchResult> SearchAsync(IHttpClientFactory factory, IConfiguration cfg, AttemptSearch filter, CancellationToken ct) =>
        await SendAsync<AttemptSearchResult>(factory, cfg, HttpMethod.Post, "/api/internal/task-attempts/search", filter, ct)
            ?? throw new HttpRequestException("Missing attempt search result.");

    internal static async Task<List<TaskAttempt>> LoadAllAsync(IHttpClientFactory factory, IConfiguration cfg,
        AttemptSearch filter, CancellationToken ct, int maxRows = 500_000)
    {
        var rows = new List<TaskAttempt>();
        var offset = global::System.Math.Max(0, filter.Skip);
        while (rows.Count < maxRows)
        {
            var page = await SearchAsync(factory, cfg, filter with { Skip = offset, Take = global::System.Math.Min(2000, maxRows - rows.Count) }, ct);
            rows.AddRange(page.Rows);
            offset += page.Rows.Length;
            if (page.Rows.Length == 0 || offset >= page.Total) return rows;
        }
        // Returning a truncated result silently makes charts and ratings wrong.
        throw new InvalidOperationException("Attempt query exceeded the bounded result limit.");
    }

    internal static async Task<AttemptStartResult> StartAsync(IHttpClientFactory factory, IConfiguration cfg,
        TaskAttempt attempt, int maxAttempts, int?[]? timeLimits, bool ignoreTimeLimit, CancellationToken ct) =>
        await SendAsync<AttemptStartResult>(factory, cfg, HttpMethod.Post, "/api/internal/task-attempts/start",
            new { attempt, maxAttempts, timeLimitsSeconds = timeLimits, ignoreTimeLimit }, ct)
            ?? throw new HttpRequestException("Missing attempt start result.");

    internal static async Task<TaskAttempt> SaveDraftAsync(IHttpClientFactory factory, IConfiguration cfg,
        TaskAttempt attempt, DateTimeOffset previousVersion, CancellationToken ct) =>
        await SendAsync<TaskAttempt>(factory, cfg, HttpMethod.Put, $"/api/internal/task-attempts/{attempt.Id:D}/draft",
            new { attempt, expectedUpdatedAt = previousVersion }, ct)
            ?? throw new HttpRequestException("Missing attempt draft result.");

    internal static async Task<TaskAttempt> SubmitAsync(IHttpClientFactory factory, IConfiguration cfg,
        TaskAttempt attempt, DateTimeOffset previousVersion, CancellationToken ct) =>
        await SendAsync<TaskAttempt>(factory, cfg, HttpMethod.Put, $"/api/internal/task-attempts/{attempt.Id:D}/submit",
            new { attempt, expectedUpdatedAt = previousVersion }, ct)
            ?? throw new HttpRequestException("Missing attempt submission result.");

    internal static Task<DeleteResult?> DeleteAsync(IHttpClientFactory factory, IConfiguration cfg, Guid attemptId, CancellationToken ct) =>
        SendAsync<DeleteResult>(factory, cfg, HttpMethod.Delete, $"/api/internal/task-attempts/{attemptId:D}", null, ct, true);

    internal static async Task<DeleteResult> DeleteUserAsync(IHttpClientFactory factory, IConfiguration cfg,
        Guid userId, string? kind, CancellationToken ct) =>
        await SendAsync<DeleteResult>(factory, cfg, HttpMethod.Delete,
            $"/api/internal/task-attempts/users/{userId:D}" + (kind is null ? "" : $"?kind={Uri.EscapeDataString(kind)}"), null, ct)
            ?? throw new HttpRequestException("Missing attempt deletion result.");

    internal static async Task<DeleteResult> DeleteAssignmentsAsync(IHttpClientFactory factory, IConfiguration cfg,
        IEnumerable<Guid> assignmentIds, CancellationToken ct)
    {
        var ids = assignmentIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new DeleteResult(0, []);
        var deleted = 0;
        var users = new HashSet<Guid>();
        foreach (var batch in ids.Chunk(2000))
        {
            var outcome = await SendAsync<DeleteResult>(factory, cfg, HttpMethod.Post,
                "/api/internal/task-attempts/delete-assignments", new { assignmentIds = batch }, ct)
                ?? throw new HttpRequestException("Missing assignment-attempt deletion result.");
            deleted += outcome.Deleted;
            foreach (var user in outcome.UserIds ?? []) users.Add(user);
        }
        return new DeleteResult(deleted, users.ToArray());
    }
}

internal sealed class InteractiveAttemptConflictException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
