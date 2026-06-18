using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuizTaskService.Data;
using QuizTaskService.Data.Entities;
using QuizTaskService.DTO;
using QuizTaskService.Services;
using static QuizTaskService.Services.Access.QuizTaskAccessService;
using static QuizTaskService.Services.Common.QuizTaskCommonService;
using static QuizTaskService.Services.Mapping.QuizTaskMappingService;

namespace QuizTaskService.Services.Serialization;

internal static class QuizTaskSerializationService
{
    internal static string? NormalizeSectionCode(string? sectionCode)
    {
        return string.IsNullOrWhiteSpace(sectionCode) ? null : sectionCode.Trim().ToUpperInvariant();
    }

    internal static string NormalizeTaskType(string? type, string? sectionCode)
    {
        if (!string.IsNullOrWhiteSpace(sectionCode) && sectionCode.Trim().StartsWith("B", StringComparison.OrdinalIgnoreCase))
        {
            return "text-answer";
        }

        return string.IsNullOrWhiteSpace(type) ? "single-choice" : type.Trim();
    }

    internal static string JsonOrDefault(JsonElement? element, string? json, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(json)) return json;
        return element.HasValue && element.Value.ValueKind != JsonValueKind.Undefined ? element.Value.GetRawText() : fallback;
    }

    internal static string ExtractExplanationText(string explanationJson)
    {
        if (string.IsNullOrWhiteSpace(explanationJson) || explanationJson == "{}") return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(explanationJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? string.Empty;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) return text.GetString() ?? string.Empty;
                if (root.TryGetProperty("markdown", out var markdown) && markdown.ValueKind == JsonValueKind.String) return markdown.GetString() ?? string.Empty;
            }
        }
        catch
        {
            return explanationJson;
        }
        return string.Empty;
    }

    internal static List<string> ExtractOptions(string dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson) || dataJson == "{}") return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                return options.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch
        {
            return new List<string>();
        }
        return new List<string>();
    }

    internal static List<string> ExtractSelectedAnswers(string answerJson)
    {
        if (string.IsNullOrWhiteSpace(answerJson) || answerJson == "{}") return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(answerJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var propName in new[] { "selected", "values", "answers" })
                {
                    if (root.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
                    {
                        return prop.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                    }
                }
            }
            if (root.ValueKind == JsonValueKind.String)
            {
                var value = root.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(value) ? new List<string>() : new List<string> { value };
            }
        }
        catch
        {
            return new List<string>();
        }
        return new List<string>();
    }

    internal static string NormalizeForCompare(string value)
    {
        return string.Join(' ', (value ?? string.Empty).Trim().Replace('ё', 'е').Replace('Ё', 'Е').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static string ExtractAnswerText(string answerJson)
    {
        if (string.IsNullOrWhiteSpace(answerJson) || answerJson == "{}") return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(answerJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? string.Empty;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var propName in new[] { "value", "text", "typedText", "answer", "correct" })
                {
                    if (root.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        return prop.GetString() ?? string.Empty;
                    }
                }

                foreach (var propName in new[] { "selected", "values", "answers" })
                {
                    if (root.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
                    {
                        return string.Join(',', prop.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                    }
                }
            }
        }
        catch
        {
            return answerJson;
        }
        return string.Empty;
    }

}
