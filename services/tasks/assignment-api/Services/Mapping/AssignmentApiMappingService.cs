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
using TaskForge.Tasks.Api.Services.Analytics;
using TaskForge.Tasks.Api.Services.Specs;
using TaskForge.Tasks.Api.Services.Testing;
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

    private static object TaskConstraintsDto(Assignment x) => new
    {
        kind = "assignment",
        required = ParseStringArrayJson(x.CodeRequiredCallsJson),
        forbidden = ParseStringArrayJson(x.CodeForbiddenCallsJson),
        note = "Это правила конкретного задания. Системная политика безопасности платформы в этот список не входит."
    };

    internal static object ToDto(Assignment x, bool includeSensitive = false, bool isSolved = false)
    {
        var type = NormalizeAssignmentType(x.Type);
        var detachedTest = type == "test";
        var executable = type is "code-test" or "image-test";
        var tests = detachedTest ? null : (includeSensitive ? ParseJson(x.TestsJson) : PublicTestsJson(x.TestsJson));
        return new
        {
            x.Id,
            x.CourseId,
            x.Title,
            x.Description,
            x.Type,
            Language = executable ? x.Language : string.Empty,
            allowedLanguages = executable ? ParseCsv(x.AllowedLanguagesCsv, x.Language) : Array.Empty<string>(),
            StarterCode = executable ? x.StarterCode : null,
            tests,
            testCases = tests,
            testsJson = includeSensitive && !detachedTest ? x.TestsJson : null,
            tags = x.Tags ?? string.Empty,
            rating = x.Rating,
            isHidden = !x.IsVisible,
            isAiDraft = false,
            lifecycleStatus = x.IsVisible ? "published" : "draft",
            taskConstraints = executable ? TaskConstraintsDto(x) : new { kind = "assignment", required = Array.Empty<string>(), forbidden = Array.Empty<string>(), note = "" },
            codeForbiddenCalls = includeSensitive && executable ? ParseStringArrayJson(x.CodeForbiddenCallsJson) : Array.Empty<string>(),
            codeRequiredCalls = includeSensitive && executable ? ParseStringArrayJson(x.CodeRequiredCallsJson) : Array.Empty<string>(),
            imageTestReferenceKey = includeSensitive && type == "image-test" ? JsonString(x.TestsJson, "imageTestReferenceKey") : null,
            imageTestSimilarityThreshold = type == "image-test" ? JsonInt(x.TestsJson, "imageTestSimilarityThreshold", 90) : (int?)null,
            analyticsSettings = includeSensitive ? ParseJson(x.AnalyticsSettingsJson) ?? AssignmentAnalyticsSettingsService.ToPublicDto(AssignmentAnalyticsSettingsService.Default()) : null,
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

    internal static object ToSolveShellDto(Assignment x, bool includeSensitive = false, bool isSolved = false)
    {
        var type = NormalizeAssignmentType(x.Type);
        var executable = type is "code-test" or "image-test";
        return new
        {
            x.Id,
            x.CourseId,
            x.Title,
            x.Type,
            Language = executable ? x.Language : string.Empty,
            allowedLanguages = executable ? ParseCsv(x.AllowedLanguagesCsv, x.Language) : Array.Empty<string>(),
            StarterCode = executable ? x.StarterCode : null,
            tags = x.Tags ?? string.Empty,
            rating = x.Rating,
            isHidden = !x.IsVisible,
            isAiDraft = false,
            lifecycleStatus = x.IsVisible ? "published" : "draft",
            taskConstraints = executable ? TaskConstraintsDto(x) : new { kind = "assignment", required = Array.Empty<string>(), forbidden = Array.Empty<string>(), note = "" },
            codeForbiddenCalls = includeSensitive && executable ? ParseStringArrayJson(x.CodeForbiddenCallsJson) : Array.Empty<string>(),
            codeRequiredCalls = includeSensitive && executable ? ParseStringArrayJson(x.CodeRequiredCallsJson) : Array.Empty<string>(),
            analyticsSettings = includeSensitive ? ParseJson(x.AnalyticsSettingsJson) ?? AssignmentAnalyticsSettingsService.ToPublicDto(AssignmentAnalyticsSettingsService.Default()) : null,
            x.IsVisible,
            x.Sort,
            canEdit = includeSensitive,
            isSolved,
            solvedByCurrentUser = isSolved,
            progressStatus = isSolved ? "solved" : "not-started",
            parts = new
            {
                statementUrl = $"/api/assignments/{x.Id:D}/statement",
                testsUrl = $"/api/assignments/{x.Id:D}/tests"
            },
            x.CreatedAt,
            x.UpdatedAt
        };
    }

    internal static object ToSolveStatementDto(Assignment x, bool includeSensitive = false)
    {
        var type = NormalizeAssignmentType(x.Type);
        var executable = type is "code-test" or "image-test";
        return new
        {
            x.Id,
            x.CourseId,
            x.Title,
            x.Description,
            tags = x.Tags ?? string.Empty,
            rating = x.Rating,
            taskConstraints = executable ? TaskConstraintsDto(x) : new { kind = "assignment", required = Array.Empty<string>(), forbidden = Array.Empty<string>(), note = "" },
            canEdit = includeSensitive,
            x.UpdatedAt
        };
    }

    internal static object ToSolveTestsDto(Assignment x, bool includeSensitive = false)
    {
        var type = NormalizeAssignmentType(x.Type);
        var detachedTest = type == "test";
        var tests = detachedTest ? null : (includeSensitive ? ParseJson(x.TestsJson) : PublicTestsJson(x.TestsJson));
        return new
        {
            x.Id,
            x.Type,
            tests,
            testCases = tests,
            testsJson = includeSensitive && !detachedTest ? x.TestsJson : null,
            imageTestReferenceKey = includeSensitive && type == "image-test" ? JsonString(x.TestsJson, "imageTestReferenceKey") : null,
            imageTestSimilarityThreshold = type == "image-test" ? JsonInt(x.TestsJson, "imageTestSimilarityThreshold", 90) : (int?)null,
            canEdit = includeSensitive,
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
        NormalizeAssignmentType(x.Type) is "code-test" or "image-test" ? x.Language : string.Empty,
        x.Rating,
        x.IsVisible,
        x.Sort);

    internal static async Task ApplyAssignmentRequestAsync(Assignment assignment, AssignmentRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var previousType = NormalizeAssignmentType(assignment.Type);
        var nextType = !string.IsNullOrWhiteSpace(request.Type) ? NormalizeExplicitAssignmentType(request.Type) : previousType;

        if (!string.IsNullOrWhiteSpace(request.Title)) assignment.Title = request.Title.Trim();
        if (request.Description != null) assignment.Description = request.Description;
        if (request.Tags != null) assignment.Tags = request.Tags;
        if (request.Rating.HasValue) assignment.Rating = System.Math.Max(0, request.Rating.Value);
        if (request.Sort.HasValue) assignment.Sort = System.Math.Max(0, request.Sort.Value);

        if (!string.Equals(previousType, nextType, StringComparison.Ordinal))
            throw new InvalidOperationException("Assignment.Type is immutable after creation.");

        switch (nextType)
        {
            case "code-test":
                await AssignmentTypeSpecService.SaveCodeFromRequestAsync(db, assignment, request, ct);
                break;
            case "image-test":
                await AssignmentTypeSpecService.SaveImageFromRequestAsync(db, assignment, request, clients, cfg, ct);
                break;
            case "test":
            {
                var incomingTestsJson = RawJson(request.Tests) ?? request.TestsJson;
                if (!string.Equals(previousType, "test", StringComparison.Ordinal))
                {
                    assignment.TestsJson = null;
                    await TestAssignmentSpecService.MergeFromRequestAsync(db, assignment, incomingTestsJson ?? "{}", ct);
                }
                else if (incomingTestsJson != null)
                {
                    await TestAssignmentSpecService.MergeFromRequestAsync(db, assignment, incomingTestsJson, ct);
                }
                else
                {
                    await TestAssignmentSpecService.EnsureDetachedAsync(db, assignment, ct);
                }
                break;
            }
            case "math":
                await AssignmentTypeSpecService.SaveMathFromRequestAsync(db, assignment, request, ct);
                break;
            case "sql-test":
                assignment.Language = string.Empty;
                assignment.AllowedLanguagesCsv = null;
                assignment.StarterCode = null;
                assignment.TestsJson = null;
                assignment.CodeForbiddenCallsJson = null;
                assignment.CodeRequiredCallsJson = null;
                break;
        }

        if (request.AnalyticsSettings.HasValue) assignment.AnalyticsSettingsJson = AssignmentAnalyticsSettingsService.NormalizeJson(request.AnalyticsSettings);
        if (request.IsVisible.HasValue) assignment.IsVisible = request.IsVisible.Value;
        if (request.IsHidden.HasValue) assignment.IsVisible = !request.IsHidden.Value;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static async Task<Assignment> BuildAssignmentEntityAsync(Guid courseId, AssignmentRequest request, int sort, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var type = string.IsNullOrWhiteSpace(request.Type) ? "code-test" : NormalizeExplicitAssignmentType(request.Type);
        var assignment = new Assignment
        {
            Id = request.Id.HasValue && request.Id.Value != Guid.Empty ? request.Id.Value : Guid.NewGuid(),
            CourseId = courseId,
            Title = Clean(request.Title, "Новое задание"),
            Description = request.Description,
            Type = type,
            Language = type switch { "code-test" => NormalizeLanguage(request.Language) ?? "csharp", "image-test" => NormalizeLanguage(request.Language) ?? "python", _ => string.Empty },
            AllowedLanguagesCsv = null,
            Tags = request.Tags,
            Rating = System.Math.Max(0, request.Rating ?? 1),
            StarterCode = null,
            TestsJson = null,
            CodeForbiddenCallsJson = null,
            CodeRequiredCallsJson = null,
            AnalyticsSettingsJson = AssignmentAnalyticsSettingsService.NormalizeJson(request.AnalyticsSettings),
            IsVisible = type != "sql-test" && (request.IsVisible ?? !(request.IsHidden ?? false)),
            Sort = request.Sort ?? sort
        };

        switch (type)
        {
            case "code-test":
                await AssignmentTypeSpecService.SaveCodeFromRequestAsync(db, assignment, request, ct);
                break;
            case "image-test":
                await AssignmentTypeSpecService.SaveImageFromRequestAsync(db, assignment, request, clients, cfg, ct);
                break;
            case "test":
            {
                var testsJson = RawJson(request.Tests) ?? request.TestsJson;
                await TestAssignmentSpecService.MergeFromRequestAsync(db, assignment, testsJson, ct);
                break;
            }
            case "math":
                await AssignmentTypeSpecService.SaveMathFromRequestAsync(db, assignment, request, ct);
                break;
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
