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
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Services.Mapping;

internal static class SolutionsApiMappingService
{
    internal static async Task<JudgeSpec?> LoadJudgeSpecAsync(Guid assignmentId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var baseUrl = ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080");
        var client = httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/internal/assignments/{assignmentId}/judge-spec");
        AddInternalKey(msg, cfg);

        try
        {
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<JudgeSpec>(JsonOptions(), ct);
        }
        catch
        {
            return null;
        }
    }

    internal static JsonElement[] ElementToArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Select(x => x.Clone()).ToArray();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var merged = new List<JsonElement>();
            if (element.TryGetProperty("publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array)
                merged.AddRange(publicTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: false)));
            if (element.TryGetProperty("hiddenTests", out var hiddenTests) && hiddenTests.ValueKind == JsonValueKind.Array)
                merged.AddRange(hiddenTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: true)));
            if (merged.Count > 0) return merged.ToArray();

            foreach (var name in new[] { "testCases", "tests", "cases" })
            {
                if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                {
                    return prop.EnumerateArray().Select(x => x.Clone()).ToArray();
                }
            }
        }
        return [];
    }

    internal static async Task<Dictionary<Guid, AssignmentMetadata>> LoadAssignmentMetadataAsync(IEnumerable<Guid> assignmentIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var ids = assignmentIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, AssignmentMetadata>();

        var assignments = await PostInternalAsync<List<AssignmentSummaryDto>>(httpFactory, cfg, ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080"), "/api/internal/assignments/summaries", new AssignmentIdsRequest(ids), ct) ?? new List<AssignmentSummaryDto>();
        var courseIds = assignments.Select(x => x.CourseId).Where(x => x != Guid.Empty).Distinct().ToArray();
        var courses = await PostInternalAsync<List<CourseSummaryDto>>(httpFactory, cfg, ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), "/api/internal/courses/metadata", new CourseIdsRequest(courseIds), ct) ?? new List<CourseSummaryDto>();
        var courseMap = courses.ToDictionary(x => x.CourseId != Guid.Empty ? x.CourseId : x.Id, x => x.Title ?? x.CourseTitle ?? "Курс");

        var map = new Dictionary<Guid, AssignmentMetadata>();
        foreach (var a in assignments)
        {
            var id = a.AssignmentId != Guid.Empty ? a.AssignmentId : a.Id;
            if (id == Guid.Empty) continue;
            courseMap.TryGetValue(a.CourseId, out var courseTitle);
            map[id] = new AssignmentMetadata(id, a.CourseId, a.Title ?? a.AssignmentTitle ?? "Задание без названия", courseTitle ?? "Курс", System.Math.Max(0, a.Rating));
        }
        return map;
    }

    internal static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryRequest("solutions-api", "identity-api", ids);
        var rows = await PostInternalAsync<List<UserSummaryDto>>(httpFactory, cfg, ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080"), "/api/internal/users/summaries", new UserIdsRequest(ids), ct) ?? new List<UserSummaryDto>();
        var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
        TaskForgeDebugTrace.UserSummaryResponse("solutions-api", "identity-api", ids, map);
        return map;
    }

    internal static async Task<Guid[]> LoadGroupMemberIdsAsync(Guid groupId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var response = await GetInternalAsync<GroupMembersResponse>(httpFactory, cfg, ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/groups/{groupId}/members", ct);
        return response?.UserIds?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>();
    }

    internal static async Task<T?> GetInternalAsync<T>(IHttpClientFactory httpFactory, IConfiguration cfg, string baseUrl, string path, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + path);
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return default;
            return await resp.Content.ReadFromJsonAsync<T>(JsonOptions(), ct);
        }
        catch
        {
            return default;
        }
    }

    internal static string UserLabel(UserSummaryDto? user)
    {
        var name = (user?.DisplayName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(name) && !LooksLikeEmail(name)) return name;
        var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (!string.IsNullOrWhiteSpace(full)) return full;
        var login = (user?.Login ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(login)) return login;
        var masked = (user?.MaskedEmail ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(masked)) return masked;
        return "Пользователь";
    }

    internal static object ToDto(SolutionSubmission x, bool includeSensitiveResult = false, AssignmentMetadata? metadata = null)
    {
        var result = SanitizeSolutionResult(ParseJsonElement(x.ResultJson), includeSensitiveResult);
        var counts = CountCases(result);
        var acceptedStatus = string.Equals(x.Status, "Accepted", StringComparison.OrdinalIgnoreCase);
        var accepted = acceptedStatus && (counts.total == 0 || counts.failed == 0);
        return new
        {
            x.Id,
            x.AssignmentId,
            assignmentTitle = metadata?.Title,
            title = metadata?.Title,
            courseId = metadata?.CourseId,
            courseTitle = metadata?.CourseTitle,
            x.UserId,
            x.Language,
            x.ExecutionTarget,
            x.SqlSpecVersionId,
            x.SqlEngineProfileId,
            kind = x.SqlSpecVersionId.HasValue ? "sql" : "code",
            x.Code,
            submittedCode = x.Code,
            verdict = accepted ? "Accepted" : x.Status,
            status = accepted ? "Accepted" : x.Status,
            x.Score,
            result = result.HasValue ? (object)result.Value : null,
            isPending = IsPendingVerdict(x.Status),
            passedAllTests = accepted,
            passedAll = accepted,
            compileError = string.Equals(x.Status, "CompileError", StringComparison.OrdinalIgnoreCase),
            policyFailed = string.Equals(x.Status, "PolicyFailed", StringComparison.OrdinalIgnoreCase),
            passedCount = counts.passed,
            failedCount = counts.failed,
            totalCount = counts.total,
            x.CreatedAt,
            createdAtUtc = x.CreatedAt,
            submittedAt = x.CreatedAt
        };
    }

    internal static (int passed, int failed, int total) CountCases(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return (0, 0, 0);
        var root = element.Value;
        JsonElement cases;
        if (root.TryGetProperty("cases", out cases) || root.TryGetProperty("results", out cases) || root.TryGetProperty("testCases", out cases))
        {
            if (cases.ValueKind == JsonValueKind.Array)
            {
                var passed = 0;
                var failed = 0;
                foreach (var item in cases.EnumerateArray())
                {
                    if (IsPassedResult(item)) passed++;
                    else failed++;
                }
                return (passed, failed, passed + failed);
            }
        }
        return (0, 0, 0);
    }

}
