using System.Text.Json;
using System.Text.RegularExpressions;
using static QuizTaskService.Services.Access.QuizTaskAccessService;
using static QuizTaskService.Services.Common.QuizTaskCommonService;
using static QuizTaskService.Services.Mapping.QuizTaskMappingService;
using static QuizTaskService.Services.Serialization.QuizTaskSerializationService;

namespace QuizTaskService.Services;

public static class QuizAnswerChecker
{
    public static bool IsCorrect(JsonElement answer, string correctAnswerJson)
    {
        try
        {
            using var correctDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(correctAnswerJson) ? "{}" : correctAnswerJson);

            var answerValues = ExtractValues(answer);
            var correctValues = ExtractValues(correctDoc.RootElement);

            if (answerValues.Count == 0 || correctValues.Count == 0)
            {
                return AreEqual(answer.GetRawText(), correctDoc.RootElement.GetRawText());
            }

            if (RequiresSetEquality(correctDoc.RootElement))
            {
                return answerValues.SetEquals(correctValues);
            }

            return answerValues.Any(answerValue => correctValues.Contains(answerValue));
        }
        catch
        {
            return false;
        }
    }

    private static bool RequiresSetEquality(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("selected", out var selected)
            && selected.ValueKind == JsonValueKind.Array;
    }

    private static HashSet<string> ExtractValues(JsonElement root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        if (root.ValueKind == JsonValueKind.String)
        {
            AddValue(result, root.GetString());
            return result;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var propName in new[] { "selected", "values", "answers", "correct" })
        {
            if (!root.TryGetProperty(propName, out var prop)) continue;

            if (prop.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AddValue(result, item.GetString());
                    }
                    else if (item.ValueKind == JsonValueKind.Number || item.ValueKind == JsonValueKind.True || item.ValueKind == JsonValueKind.False)
                    {
                        AddValue(result, item.ToString());
                    }
                }
            }
            else if (prop.ValueKind == JsonValueKind.String)
            {
                AddValue(result, prop.GetString());
            }
            else if (prop.ValueKind == JsonValueKind.Number || prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
            {
                AddValue(result, prop.ToString());
            }
        }

        foreach (var propName in new[] { "value", "text", "typedText", "answer" })
        {
            if (!root.TryGetProperty(propName, out var prop)) continue;

            if (prop.ValueKind == JsonValueKind.String)
            {
                AddValue(result, prop.GetString());
            }
            else if (prop.ValueKind == JsonValueKind.Number || prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
            {
                AddValue(result, prop.ToString());
            }
        }

        return result;
    }

    private static void AddValue(HashSet<string> values, string? value)
    {
        var normalized = Normalize(value ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            values.Add(normalized);
        }
    }

    private static bool AreEqual(string left, string right)
    {
        return Normalize(left) == Normalize(right);
    }

    private static string Normalize(string value)
    {
        var normalized = value
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');

        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        normalized = Regex.Replace(normalized, @"[\.,;:!\?]+$", string.Empty).Trim();

        return normalized;
    }
}
