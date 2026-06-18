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
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Mapping;

internal static class AssignmentApiMappingService
{
    internal static async Task<IResult> SaveSpec(Guid assignmentId, JsonElement payload, TasksDbContext db, string kind)
    {
        var assignment = await db.Assignments.FindAsync(assignmentId);
        if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        NormalizeIds(node, kind == "test" ? "questions" : "blocks");
        assignment.TestsJson = node.ToJsonString(JsonOptions());
        assignment.Type = kind;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Microsoft.AspNetCore.Http.Results.Ok(kind == "test" ? TaskSpecToJsonObject(ParseTaskSpec(node)) : MathSpecToJsonObject(ParseMathSpec(node)));
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

    internal static object ToDto(Assignment x, bool includeSensitive = false, bool isSolved = false)
    {
        var tests = includeSensitive ? ParseJson(x.TestsJson) : PublicTestsJson(x.TestsJson);
        return new
        {
            x.Id,
            x.CourseId,
            x.Title,
            x.Description,
            x.Type,
            x.Language,
            allowedLanguages = ParseCsv(x.AllowedLanguagesCsv, x.Language),
            x.StarterCode,
            tests,
            testCases = tests,
            testsJson = includeSensitive ? x.TestsJson : null,
            tags = x.Tags ?? string.Empty,
            difficulty = x.Difficulty,
            rating = x.Rating,
            isHidden = !x.IsVisible,
            isAiDraft = false,
            lifecycleStatus = x.IsVisible ? "published" : "draft",
            codeForbiddenCalls = includeSensitive ? ParseStringArrayJson(x.CodeForbiddenCallsJson) : Array.Empty<string>(),
            codeRequiredCalls = includeSensitive ? ParseStringArrayJson(x.CodeRequiredCallsJson) : Array.Empty<string>(),
            imageTestReferenceKey = includeSensitive ? JsonString(x.TestsJson, "imageTestReferenceKey") : null,
            imageTestSimilarityThreshold = JsonInt(x.TestsJson, "imageTestSimilarityThreshold", 90),
            x.IsVisible,
            x.Sort,
            canEdit = includeSensitive,
            isSolved,
            solvedByCurrentUser = isSolved,
            progressStatus = isSolved ? "solved" : "not-started",
            x.CreatedAt,
            x.UpdatedAt
        };
    }

    internal static AssignmentSummaryDto ToAssignmentSummaryDto(Assignment x) => new(
        x.Id,
        x.Id,
        x.CourseId,
        x.Title,
        x.Title,
        x.Type,
        x.Language,
        x.Rating,
        x.Difficulty,
        x.IsVisible,
        x.Sort);

    internal static async Task ApplyAssignmentRequestAsync(Assignment assignment, AssignmentRequest request, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Title)) assignment.Title = request.Title.Trim();
        if (request.Description != null) assignment.Description = request.Description;
        if (!string.IsNullOrWhiteSpace(request.Type)) assignment.Type = NormalizeAssignmentType(request.Type);
        if (!string.IsNullOrWhiteSpace(request.Language)) assignment.Language = NormalizeLanguage(request.Language) ?? assignment.Language;
        if (request.AllowedLanguages != null) assignment.AllowedLanguagesCsv = NormalizeLanguagesCsv(request.AllowedLanguages);
        if (request.Tags != null) assignment.Tags = request.Tags;
        if (request.Difficulty.HasValue) assignment.Difficulty = System.Math.Clamp(request.Difficulty.Value, 1, 3);
        if (request.Rating.HasValue) assignment.Rating = System.Math.Max(0, request.Rating.Value);
        if (request.Sort.HasValue) assignment.Sort = System.Math.Max(0, request.Sort.Value);
        if (request.StarterCode != null) assignment.StarterCode = request.StarterCode;

        var nextType = !string.IsNullOrWhiteSpace(request.Type) ? NormalizeAssignmentType(request.Type) : assignment.Type;
        if (string.Equals(nextType, "image-test", StringComparison.OrdinalIgnoreCase))
        {
            var hasRealSpec = HasMeaningfulJsonText(request.TestsJson) || HasMeaningfulJsonElement(request.Tests) || HasMeaningfulJsonElement(request.TestCases);
            if (hasRealSpec || request.ImageTestReferenceKey != null || request.ImageTestSimilarityThreshold.HasValue)
            {
                assignment.TestsJson = (await MergeAndMaterializeImageTestPayloadAsync(hasRealSpec ? request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases) : assignment.TestsJson, request, assignment.Id, clients, cfg, ct)).ToJsonString(JsonOptions());
            }
        }
        else if (request.TestsJson != null || request.Tests.HasValue || request.TestCases.HasValue)
        {
            assignment.TestsJson = NormalizeSpecJsonForStorage(request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases), nextType);
        }

        if (request.CodeForbiddenCalls != null) assignment.CodeForbiddenCallsJson = StringArrayJson(request.CodeForbiddenCalls);
        if (request.CodeRequiredCalls != null) assignment.CodeRequiredCallsJson = StringArrayJson(request.CodeRequiredCalls);
        if (request.IsVisible.HasValue) assignment.IsVisible = request.IsVisible.Value;
        if (request.IsHidden.HasValue) assignment.IsVisible = !request.IsHidden.Value;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static async Task<Assignment> BuildAssignmentEntityAsync(Guid courseId, AssignmentRequest request, int sort, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var type = NormalizeAssignmentType(request.Type);
        var testsJson = request.TestsJson ?? RawJson(request.Tests) ?? RawJson(request.TestCases);
        testsJson = NormalizeSpecJsonForStorage(testsJson, type);
        var assignment = new Assignment
        {
            Id = request.Id.HasValue && request.Id.Value != Guid.Empty ? request.Id.Value : Guid.NewGuid(),
            CourseId = courseId,
            Title = Clean(request.Title, "Новое задание"),
            Description = request.Description,
            Type = type,
            Language = NormalizeLanguage(request.Language) ?? (type == "image-test" ? "python" : "csharp"),
            AllowedLanguagesCsv = NormalizeLanguagesCsv(request.AllowedLanguages),
            Tags = request.Tags,
            Difficulty = System.Math.Clamp(request.Difficulty ?? 1, 1, 3),
            Rating = System.Math.Max(0, request.Rating ?? 1),
            StarterCode = request.StarterCode,
            TestsJson = type == "image-test" ? null : testsJson,
            CodeForbiddenCallsJson = StringArrayJson(request.CodeForbiddenCalls),
            CodeRequiredCallsJson = StringArrayJson(request.CodeRequiredCalls),
            IsVisible = request.IsVisible ?? !(request.IsHidden ?? false),
            Sort = request.Sort ?? sort
        };

        if (type == "image-test")
        {
            assignment.TestsJson = (await MergeAndMaterializeImageTestPayloadAsync(testsJson, request, assignment.Id, clients, cfg, ct)).ToJsonString(JsonOptions());
        }

        return assignment;
    }

    internal static async Task<string> LoadCourseTitleAsync(Guid courseId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (courseId == Guid.Empty) return "Курс";
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "EducationApi", "http://education-api:8080")}/api/internal/courses/metadata")
            {
                Content = JsonContent.Create(new CourseIdsRequest(new[] { courseId }), options: JsonOptions())
            };
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return "Курс";
            var rows = await resp.Content.ReadFromJsonAsync<List<CourseSummaryDto>>(JsonOptions(), ct) ?? new List<CourseSummaryDto>();
            var row = rows.FirstOrDefault();
            return string.IsNullOrWhiteSpace(row?.Title ?? row?.CourseTitle) ? "Курс" : (row!.Title ?? row.CourseTitle)!;
        }
        catch { return "Курс"; }
    }

    internal static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(1000).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryRequest("tasks-api", "identity-api", ids);
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080")}/api/internal/users/summaries")
            {
                Content = JsonContent.Create(new UserIdsRequest(ids), options: JsonOptions())
            };
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var empty = new Dictionary<Guid, UserSummaryDto>();
                TaskForgeDebugTrace.UserSummaryResponse("tasks-api", "identity-api", ids, empty);
                return empty;
            }
            var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
            var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
            TaskForgeDebugTrace.UserSummaryResponse("tasks-api", "identity-api", ids, map);
            return map;
        }
        catch
        {
            var empty = new Dictionary<Guid, UserSummaryDto>();
            TaskForgeDebugTrace.UserSummaryResponse("tasks-api", "identity-api", ids, empty);
            return empty;
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

}
