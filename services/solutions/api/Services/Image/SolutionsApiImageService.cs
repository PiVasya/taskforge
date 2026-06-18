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
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Services.Image;

internal static class SolutionsApiImageService
{
    internal static object ImageDto(UserImageTaskSolution x, bool includeReference = false)
    {
        var result = ParseJsonElement(x.ResultJson);
        if (!includeReference) result = SanitizeImageResult(result);
        var similarity = TryReadNumber(result, "similarityPercent") ?? TryReadNumber(result, "similarity") ?? x.SimilarityPercent;
        var threshold = TryReadNumber(result, "thresholdPercent") ?? TryReadNumber(result, "threshold");
        return new
        {
            x.Id,
            x.UserId,
            x.AssignmentId,
            x.Language,
            x.Code,
            submittedCode = x.Code,
            similarityPercent = similarity,
            thresholdPercent = threshold,
            x.Passed,
            result = result.HasValue ? (object)result.Value : null,
            referenceUrl = includeReference ? TryReadString(result, "referenceUrl") ?? TryReadString(result, "expectedUrl") : null,
            submittedUrl = TryReadString(result, "submittedUrl") ?? TryReadString(result, "actualUrl") ?? TryReadString(result, "renderedUrl"),
            stdout = TryReadString(result, "stdout"),
            stderr = TryReadString(result, "stderr"),
            runnerError = TryReadString(result, "runnerError") ?? TryReadString(result, "error"),
            x.CreatedAt,
            createdAtUtc = x.CreatedAt,
            submittedAt = x.CreatedAt
        };
    }

    internal static JsonElement? SanitizeImageResult(JsonElement? result)
    {
        if (!result.HasValue) return null;
        try
        {
            var node = JsonNode.Parse(result.Value.GetRawText());
            RemoveReferenceFields(node);
            return JsonSerializer.SerializeToElement(node, JsonOptions());
        }
        catch
        {
            return null;
        }
    }

}
