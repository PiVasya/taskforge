using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TaskForge.Execution.Worker;

public sealed partial class Worker
{
    private static string[] ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildPolicyMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Код содержит запрещённые конструкции.";
        }

        var kind = ReadString(root, "policyKind") ?? string.Empty;
        if (string.Equals(kind, "platform", StringComparison.OrdinalIgnoreCase))
        {
            return "Решение отклонено системой безопасности.";
        }

        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return "Код содержит запрещённые конструкции.";
        }

        var visibleErrors = errors.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Where(e => !IsSensitivePolicyPattern(ReadString(e, "pattern_id") ?? ReadString(e, "patternId")))
            .ToArray();

        var lines = visibleErrors
            .Select(e => ReadString(e, "message"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        if (visibleErrors.Any(IsCyrillicPolicyError))
        {
            return lines.Length == 0
                ? "В исполняемом коде найдена кириллица. Используйте латинские имена переменных, функций и классов."
                : string.Join("; ", lines);
        }

        return lines.Length == 0
            ? "Код не соответствует правилам задания."
            : "Код не соответствует правилам задания: " + string.Join("; ", lines);
    }


    private static bool IsCyrillicPolicyError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return false;
        var code = ReadString(error, "code") ?? string.Empty;
        var patternId = ReadString(error, "pattern_id") ?? ReadString(error, "patternId") ?? string.Empty;
        var message = ReadString(error, "message") ?? string.Empty;
        return code.Contains("cyrillic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(patternId, "unicode.cyrillic_in_code", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Кириллиц", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement BuildClientPolicyPayload(JsonElement root)
    {
        var visibleErrors = new List<Dictionary<string, object?>>();
        var visibleHits = new List<Dictionary<string, object?>>();
        var visiblePatternIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sensitiveCount = 0;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errors.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;

                var code = ReadString(e, "code") ?? "policy_failed";
                var message = ReadString(e, "message") ?? "Код содержит запрещённые конструкции.";
                var patternId = ReadString(e, "pattern_id") ?? ReadString(e, "patternId") ?? "-";

                if (IsSensitivePolicyPattern(patternId))
                {
                    sensitiveCount++;
                    continue;
                }

                visiblePatternIds.Add(patternId);
                visibleErrors.Add(new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["pattern_id"] = patternId
                });
            }
        }

        if (sensitiveCount > 0)
        {
            visibleErrors.Add(new Dictionary<string, object?>
            {
                ["code"] = "sandbox_security",
                ["message"] = visibleErrors.Count == 0
                    ? "Код использует системные возможности, которые нельзя запускать в песочнице."
                    : "Дополнительно код использует системные возможности, которые нельзя запускать в песочнице.",
                ["pattern_id"] = "platform.security"
            });
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hits.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object) continue;
                var patternId = ReadString(h, "pattern_id") ?? ReadString(h, "patternId") ?? "-";
                if (IsSensitivePolicyPattern(patternId)) continue;
                if (visiblePatternIds.Count > 0 && !visiblePatternIds.Contains(patternId)) continue;

                visibleHits.Add(new Dictionary<string, object?>
                {
                    ["pattern_id"] = patternId,
                    ["needle"] = ReadString(h, "needle") ?? string.Empty,
                    ["position"] = ReadInt(h, "position"),
                    ["preview"] = ReadString(h, "preview") ?? string.Empty
                });
            }
        }

        static bool IsVisibleTaskError(Dictionary<string, object?> e)
        {
            return !e.TryGetValue("pattern_id", out var pattern)
                || !string.Equals(pattern?.ToString(), "platform.security", StringComparison.OrdinalIgnoreCase);
        }

        var policyKind = visibleErrors.Any(IsVisibleTaskError)
            ? (sensitiveCount > 0 ? "mixed" : "task")
            : "platform";

        return CloneJson(JsonSerializer.Serialize(new
        {
            ok = false,
            policyKind,
            sensitivePlatformViolations = sensitiveCount,
            errors = visibleErrors,
            hits = visibleHits
        }, JsonOptions))!.Value;
    }

    private static bool IsSensitivePolicyPattern(string? patternId)
    {
        var id = (patternId ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) || id == "-") return false;

        return id.StartsWith("py.")
            || id.StartsWith("js.")
            || id.StartsWith("c.")
            || id.StartsWith("cpp.")
            || id.StartsWith("cs.")
            || id.StartsWith("java.")
            || id.StartsWith("pas.");
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
        return null;
    }

}
