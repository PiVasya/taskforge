using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Analytics;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Authoring;

internal static class AssignmentAuthoringCommonService
{
    internal static Assignment BuildBase(Guid courseId, string type, AssignmentMetadataAuthoringRequest request, int defaultSort)
        => new()
        {
            Id = request.Id is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
            CourseId = courseId,
            Title = Clean(request.Title, "Новое задание"),
            Description = request.Description,
            Type = type,
            Language = type switch
            {
                "code-test" => "csharp",
                "image-test" => "python",
                _ => string.Empty
            },
            Tags = request.Tags,
            Rating = System.Math.Max(0, request.Rating ?? 1),
            AnalyticsSettingsJson = AssignmentAnalyticsSettingsService.NormalizeJson(request.AnalyticsSettings),
            IsVisible = type != "sql-test" && (request.IsVisible ?? !(request.IsHidden ?? false)),
            Sort = request.Sort ?? defaultSort
        };

    internal static void ApplyMetadata(Assignment assignment, AssignmentMetadataAuthoringRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Title)) assignment.Title = request.Title.Trim();
        if (request.Description is not null) assignment.Description = request.Description;
        if (request.Tags is not null) assignment.Tags = request.Tags;
        if (request.Rating.HasValue) assignment.Rating = System.Math.Max(0, request.Rating.Value);
        if (request.Sort.HasValue) assignment.Sort = System.Math.Max(0, request.Sort.Value);
        if (request.AnalyticsSettings.HasValue)
            assignment.AnalyticsSettingsJson = AssignmentAnalyticsSettingsService.NormalizeJson(request.AnalyticsSettings);
        if (request.IsVisible.HasValue) assignment.IsVisible = request.IsVisible.Value;
        if (request.IsHidden.HasValue) assignment.IsVisible = !request.IsHidden.Value;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static JsonObject BuildEditMetaDto(Assignment assignment)
    {
        var type = TryNormalizeAssignmentType(assignment.Type, out var normalized)
            ? normalized
            : (assignment.Type ?? string.Empty).Trim().ToLowerInvariant();
        return BuildCommonEditNode(assignment, type);
    }

    internal static JsonObject BuildCommonEditNode(Assignment assignment, string type)
        => new()
        {
            ["id"] = assignment.Id.ToString(),
            ["courseId"] = assignment.CourseId.ToString(),
            ["title"] = assignment.Title,
            ["description"] = assignment.Description ?? string.Empty,
            ["type"] = type,
            ["tags"] = assignment.Tags ?? string.Empty,
            ["rating"] = assignment.Rating,
            ["isHidden"] = !assignment.IsVisible,
            ["isVisible"] = assignment.IsVisible,
            ["isAiDraft"] = false,
            ["lifecycleStatus"] = assignment.IsVisible ? "published" : "draft",
            ["sort"] = assignment.Sort,
            ["analyticsSettings"] = ParseJsonNode(assignment.AnalyticsSettingsJson)
                ?? JsonSerializer.SerializeToNode(AssignmentAnalyticsSettingsService.ToPublicDto(AssignmentAnalyticsSettingsService.Default()), JsonOptions()),
            ["canEdit"] = true,
            ["createdAt"] = JsonSerializer.SerializeToNode(assignment.CreatedAt, JsonOptions()),
            ["updatedAt"] = JsonSerializer.SerializeToNode(assignment.UpdatedAt, JsonOptions())
        };

    internal static void ClearLegacyPayload(Assignment assignment, bool keepLanguage)
    {
        if (!keepLanguage) assignment.Language = string.Empty;
        assignment.AllowedLanguagesCsv = null;
        assignment.StarterCode = null;
        assignment.TestsJson = null;
        assignment.CodeForbiddenCallsJson = null;
        assignment.CodeRequiredCallsJson = null;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
