using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TaskForge.Execution.Worker;

public sealed partial class Worker
{
    private static readonly Regex TmpTaskforgePathRegex = new(@"/tmp/taskforge-[^\s:]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TmpGoBuildPathRegex = new(@"/tmp/go-build[^\s:]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AppPathRegex = new(@"/app/[^\s:]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string SanitizeRunnerText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var s = TmpTaskforgePathRegex.Replace(value, "[временный файл]");
        s = TmpGoBuildPathRegex.Replace(s, "[временный файл]");
        s = AppPathRegex.Replace(s, "[внутренний файл]");
        return s;
    }

    private static string FriendlyRunnerText(string? value)
    {
        var s = SanitizeRunnerText(value);
        if (s.Contains("fork/exec", StringComparison.OrdinalIgnoreCase)
            && s.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "Не удалось запустить программу: нет прав на выполнение файла проверки.";
        }
        return s;
    }

    private static JsonElement SanitizeRunnerPayload(JsonElement root)
    {
        try
        {
            var node = JsonNode.Parse(root.GetRawText());
            if (node is null) return root;
            SanitizeRunnerNode(node);
            return CloneJson(node.ToJsonString(JsonOptions)) ?? root;
        }
        catch
        {
            return root;
        }
    }

    private static void SanitizeRunnerNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    if (kv.Value is JsonValue value && value.TryGetValue<string>(out var textValue))
                    {
                        if (ShouldSanitizeRunnerField(kv.Key)) obj[kv.Key] = FriendlyRunnerText(textValue);
                    }
                    else
                    {
                        SanitizeRunnerNode(kv.Value);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                {
                    SanitizeRunnerNode(child);
                }
                break;
        }
    }

    private static bool ShouldSanitizeRunnerField(string? name)
    {
        var key = (name ?? string.Empty).Trim().ToLowerInvariant();
        return key == "stderr"
            || key == "compilestderr"
            || key == "compileerror"
            || key == "error"
            || key == "message"
            || key == "detail"
            || key == "errormessage"
            || key.Contains("exception")
            || key.Contains("stacktrace");
    }

    private static JsonElement? ExtractResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "results", "testCases", "cases" })
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop.Clone();
            }
        }
        return null;
    }

    private static bool IsPassedResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty("passed", out var passed) && passed.ValueKind is JsonValueKind.True or JsonValueKind.False) return passed.GetBoolean();
        if (item.TryGetProperty("status", out var status)) return string.Equals(status.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool IsCompileErrorResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty("status", out var status) && string.Equals(status.ToString(), "compile_error", StringComparison.OrdinalIgnoreCase)) return true;
        if (item.TryGetProperty("compileStderr", out var compileStderr) && !string.IsNullOrWhiteSpace(compileStderr.ToString())) return true;
        return false;
    }

    private static bool IsCompileErrorRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("status", out var status))
        {
            var value = status.ToString();
            if (string.Equals(value, "compile_error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "compilation_error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "compileerror", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (root.TryGetProperty("compileStderr", out var compileStderr) && !string.IsNullOrWhiteSpace(compileStderr.ToString())) return true;
        if (root.TryGetProperty("stderr", out var stderr) && root.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number && exitCode.GetInt32() != 0 && !string.IsNullOrWhiteSpace(stderr.ToString())) return true;
        return false;
    }

    private static bool IsJudgeUnavailableResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;

        if (item.TryGetProperty("status", out var status)
            && IsJudgeUnavailableStatus(status.ToString()))
        {
            return true;
        }

        return ContainsRunnerInfrastructureDiagnostic(item);
    }

    private static bool IsJudgeUnavailableRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (root.TryGetProperty("status", out var status)
            && IsJudgeUnavailableStatus(status.ToString()))
        {
            return true;
        }

        return ContainsRunnerInfrastructureDiagnostic(root);
    }

    private static bool IsJudgeUnavailableStatus(string? value)
        => string.Equals(value, "judge_unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "runner_unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "infrastructure_error", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsRunnerInfrastructureDiagnostic(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return IsRunnerInfrastructureDiagnostic(value.GetString());
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals("actualOutput")
                    || property.NameEquals("stderr")
                    || property.NameEquals("error")
                    || property.NameEquals("message")
                    || property.NameEquals("detail")
                    || property.NameEquals("errorMessage"))
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && IsRunnerInfrastructureDiagnostic(property.Value.GetString()))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsRunnerInfrastructureDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        return value.Contains("Too many open files", StringComparison.OrdinalIgnoreCase)
            || value.Contains("error=24", StringComparison.OrdinalIgnoreCase)
            || value.Contains("EMFILE", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ENFILE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPolicyErrorResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        return item.TryGetProperty("status", out var status)
            && (string.Equals(status.ToString(), "policy_error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.ToString(), "policy_failed", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPolicyErrorRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        return root.TryGetProperty("status", out var status)
            && (string.Equals(status.ToString(), "policy_error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.ToString(), "policy_failed", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement? CloneJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

}
