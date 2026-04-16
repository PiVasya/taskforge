using System.Globalization;
using System.Text.Json;
using taskforge.Data.Models.DTO.AI;

namespace taskforge.Services.AI;

internal static class AiAssignmentOverviewHelper
{
    public const string CourseOverviewKind = "course-overview";
    public const string LegacyOverviewKind = "assignment-overview";

    public static bool IsOverviewKind(string? kind)
        => string.Equals((kind ?? string.Empty).Trim(), CourseOverviewKind, StringComparison.OrdinalIgnoreCase)
           || string.Equals((kind ?? string.Empty).Trim(), LegacyOverviewKind, StringComparison.OrdinalIgnoreCase);

    public static AiAssignmentOverviewDto? ParseOverview(string? suggestionsJson, string? summary, DateTime? createdAtUtc = null)
    {
        var dto = new AiAssignmentOverviewDto
        {
            Summary = string.IsNullOrWhiteSpace(summary) ? string.Empty : summary.Trim(),
            CreatedAtUtc = createdAtUtc,
        };

        if (!string.IsNullOrWhiteSpace(suggestionsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(suggestionsJson);
                var root = doc.RootElement;
                var overview = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("overview", out var ov) && ov.ValueKind == JsonValueKind.Object
                    ? ov
                    : root;
                Hydrate(dto, overview);
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("suggestions", out var suggestions) && suggestions.ValueKind == JsonValueKind.Array)
                        dto.Suggestions = ReadStringList(suggestions);
                    if (dto.ImportanceReasons.Count == 0 && root.TryGetProperty("importanceReasons", out var rootReasons) && rootReasons.ValueKind == JsonValueKind.Array)
                        dto.ImportanceReasons = ReadStringList(rootReasons);
                }
            }
            catch
            {
                // keep summary-only fallback
            }
        }

        if (!dto.IsMeaningful())
            return null;
        return dto;
    }

    public static string BuildStoredPayloadJson(JsonElement root)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();

        writer.WritePropertyName("overview");
        writer.WriteStartObject();
        if (root.TryGetProperty("overview", out var nestedOverview) && nestedOverview.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in nestedOverview.EnumerateObject())
                property.WriteTo(writer);
        }
        else
        {
            WriteIfPresent(root, writer, "isImportant");
            WriteIfPresent(root, writer, "importanceScore");
            WriteIfPresent(root, writer, "importanceReasons");
            WriteIfPresent(root, writer, "pedagogicalRole");
            WriteIfPresent(root, writer, "teachingStyle");
            WriteIfPresent(root, writer, "studentStage");
            WriteIfPresent(root, writer, "conceptsIntroduced");
            WriteIfPresent(root, writer, "conceptsReinforced");
            WriteIfPresent(root, writer, "prerequisites");
            WriteIfPresent(root, writer, "surfaceSignals");
            WriteIfPresent(root, writer, "courseValue");
        }
        writer.WriteEndObject();

        if (root.TryGetProperty("suggestions", out var suggestions) && suggestions.ValueKind == JsonValueKind.Array)
        {
            writer.WritePropertyName("suggestions");
            suggestions.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string BuildFallbackSummary(JsonElement root, string? title = null)
    {
        var explicitSummary = ReadString(root, "summary");
        if (!string.IsNullOrWhiteSpace(explicitSummary))
            return explicitSummary!;

        var reasons = ReadStringList(root, "importanceReasons");
        var courseValue = ReadString(root, "courseValue");
        var pedagogicalRole = ReadString(root, "pedagogicalRole");
        var head = reasons.Count > 0 ? reasons[0] : (courseValue ?? pedagogicalRole ?? "AI overview построен автоматически.");
        var prefix = string.IsNullOrWhiteSpace(title) ? string.Empty : $"{title.Trim()}: ";
        var summary = (prefix + head).Trim();
        return summary.Length > 220 ? summary[..220] : summary;
    }

    private static void WriteIfPresent(JsonElement root, Utf8JsonWriter writer, string name)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    private static void Hydrate(AiAssignmentOverviewDto dto, JsonElement overview)
    {
        dto.IsImportant = ReadBool(overview, "isImportant") ?? dto.IsImportant;
        dto.ImportanceScore = ReadDouble(overview, "importanceScore") ?? dto.ImportanceScore;
        dto.PedagogicalRole = ReadString(overview, "pedagogicalRole") ?? dto.PedagogicalRole;
        dto.TeachingStyle = ReadString(overview, "teachingStyle") ?? dto.TeachingStyle;
        dto.StudentStage = ReadString(overview, "studentStage") ?? dto.StudentStage;
        dto.CourseValue = ReadString(overview, "courseValue") ?? dto.CourseValue;
        dto.ImportanceReasons = ReadStringList(overview, "importanceReasons");
        dto.ConceptsIntroduced = ReadStringList(overview, "conceptsIntroduced", "introducedConcepts", "keyConcepts");
        dto.ConceptsReinforced = ReadStringList(overview, "conceptsReinforced", "reinforcedConcepts");
        dto.Prerequisites = ReadStringList(overview, "prerequisites", "requiresConcepts");
        dto.Signals = ReadStringList(overview, "surfaceSignals", "signals");
        if (!dto.IsImportant && dto.ImportanceScore.HasValue && dto.ImportanceScore.Value >= 0.7d)
            dto.IsImportant = true;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fromString))
            return fromString;
        return null;
    }

    private static List<string> ReadStringList(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                return ReadStringList(value);
        }
        return new List<string>();
    }

    private static List<string> ReadStringList(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return array.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => (x.GetString() ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
    }
}
