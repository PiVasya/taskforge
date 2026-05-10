using System.Text.Json;
using System.Text.Json.Nodes;

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
                return Normalize(answer.GetRawText()) == Normalize(correctDoc.RootElement.GetRawText());
            }

            return answerValues.SetEquals(correctValues);
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<string> ExtractValues(JsonElement root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind == JsonValueKind.String)
        {
            result.Add(Normalize(root.GetString() ?? string.Empty));
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
                        result.Add(Normalize(item.GetString() ?? string.Empty));
                    }
                }
            }
            else if (prop.ValueKind == JsonValueKind.String)
            {
                result.Add(Normalize(prop.GetString() ?? string.Empty));
            }
        }

        foreach (var propName in new[] { "value", "text", "typedText" })
        {
            if (root.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                result.Add(Normalize(prop.GetString() ?? string.Empty));
            }
        }

        return result;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant().Replace("ё", "е");
    }
}
