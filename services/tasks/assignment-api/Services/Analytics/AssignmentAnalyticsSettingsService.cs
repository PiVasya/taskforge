using System.Text.Json;
using TaskForge.Tasks.Api.Services.Serialization;

namespace TaskForge.Tasks.Api.Services.Analytics;

internal sealed class AssignmentAnalyticsSettings
{
    public string Mode { get; set; } = "basic";
    public bool TrackOpen { get; set; } = true;
    public bool TrackAttempts { get; set; } = true;
    public bool TrackTime { get; set; } = true;
    public bool TrackLanguage { get; set; } = true;
    public bool TrackErrors { get; set; } = true;
    public bool TrackEditorChanges { get; set; }
    public bool TrackClipboard { get; set; }
    public bool TrackFocus { get; set; }
    public bool TrackVisibility { get; set; }
    public bool TrackFullscreen { get; set; }
    public bool TrackCodeSnapshots { get; set; }
    public bool TrackRiskScore { get; set; }
    public bool TrackLiveActivity { get; set; }
    public bool TrackSimilarity { get; set; }
    public bool StoreFullCode { get; set; }
    public bool StorePasteText { get; set; }
    public bool StoreTextSamples { get; set; } = true;
    public int PasteSampleLimit { get; set; } = 500;
    public int CodeSampleLimit { get; set; } = 1000;
    public int CodeSnapshotIntervalSeconds { get; set; } = 45;
    public int EventBatchIntervalSeconds { get; set; } = 10;
    public int RetentionDays { get; set; } = 30;
    public int MaxFullCodeLength { get; set; } = 80000;
    public int MaxEventsPerBatch { get; set; } = 120;
}

internal static class AssignmentAnalyticsSettingsService
{
    internal static AssignmentAnalyticsSettings Default() => Preset("basic");

    internal static AssignmentAnalyticsSettings Preset(string? mode)
    {
        var normalized = NormalizeMode(mode);
        var s = new AssignmentAnalyticsSettings { Mode = normalized };
        switch (normalized)
        {
            case "off":
                s.TrackOpen = false;
                s.TrackAttempts = false;
                s.TrackTime = false;
                s.TrackLanguage = false;
                s.TrackErrors = false;
                s.StoreTextSamples = false;
                break;
            case "solution":
                s.TrackEditorChanges = true;
                s.TrackCodeSnapshots = true;
                s.TrackSimilarity = true;
                break;
            case "proctoring":
                s.TrackEditorChanges = true;
                s.TrackClipboard = true;
                s.TrackFocus = true;
                s.TrackVisibility = true;
                s.TrackFullscreen = true;
                s.TrackCodeSnapshots = true;
                s.TrackRiskScore = true;
                s.TrackLiveActivity = true;
                s.TrackSimilarity = true;
                break;
            case "custom":
            case "basic":
            default:
                break;
        }
        return s;
    }

    internal static string NormalizeJson(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return ToJson(Default());
        var incoming = element.Value;
        if (incoming.ValueKind != JsonValueKind.Object) return ToJson(Default());

        var mode = ReadString(incoming, "mode", "analyticsMode") ?? "basic";
        var settings = Preset(mode);
        settings.Mode = NormalizeMode(mode);
        settings.TrackOpen = ReadBool(incoming, "trackOpen", settings.TrackOpen);
        settings.TrackAttempts = ReadBool(incoming, "trackAttempts", settings.TrackAttempts);
        settings.TrackTime = ReadBool(incoming, "trackTime", settings.TrackTime);
        settings.TrackLanguage = ReadBool(incoming, "trackLanguage", settings.TrackLanguage);
        settings.TrackErrors = ReadBool(incoming, "trackErrors", settings.TrackErrors);
        settings.TrackEditorChanges = ReadBool(incoming, "trackEditorChanges", settings.TrackEditorChanges);
        settings.TrackClipboard = ReadBool(incoming, "trackClipboard", settings.TrackClipboard);
        settings.TrackFocus = ReadBool(incoming, "trackFocus", settings.TrackFocus);
        settings.TrackVisibility = ReadBool(incoming, "trackVisibility", settings.TrackVisibility);
        settings.TrackFullscreen = ReadBool(incoming, "trackFullscreen", settings.TrackFullscreen);
        settings.TrackCodeSnapshots = ReadBool(incoming, "trackCodeSnapshots", settings.TrackCodeSnapshots);
        settings.TrackRiskScore = ReadBool(incoming, "trackRiskScore", settings.TrackRiskScore);
        settings.TrackLiveActivity = ReadBool(incoming, "trackLiveActivity", settings.TrackLiveActivity);
        settings.TrackSimilarity = ReadBool(incoming, "trackSimilarity", settings.TrackSimilarity);
        settings.StoreFullCode = ReadBool(incoming, "storeFullCode", settings.StoreFullCode);
        settings.StorePasteText = ReadBool(incoming, "storePasteText", settings.StorePasteText);
        settings.StoreTextSamples = ReadBool(incoming, "storeTextSamples", settings.StoreTextSamples);
        settings.PasteSampleLimit = Clamp(ReadInt(incoming, "pasteSampleLimit", settings.PasteSampleLimit), 0, 5000);
        settings.CodeSampleLimit = Clamp(ReadInt(incoming, "codeSampleLimit", settings.CodeSampleLimit), 0, 5000);
        settings.CodeSnapshotIntervalSeconds = Clamp(ReadInt(incoming, "codeSnapshotIntervalSeconds", settings.CodeSnapshotIntervalSeconds), 10, 600);
        settings.EventBatchIntervalSeconds = Clamp(ReadInt(incoming, "eventBatchIntervalSeconds", settings.EventBatchIntervalSeconds), 3, 120);
        settings.RetentionDays = Clamp(ReadInt(incoming, "retentionDays", settings.RetentionDays), 1, 365);
        settings.MaxFullCodeLength = Clamp(ReadInt(incoming, "maxFullCodeLength", settings.MaxFullCodeLength), 0, 200000);
        settings.MaxEventsPerBatch = Clamp(ReadInt(incoming, "maxEventsPerBatch", settings.MaxEventsPerBatch), 10, 500);
        return ToJson(settings);
    }

    internal static AssignmentAnalyticsSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var normalizedJson = NormalizeJson(doc.RootElement);
            return JsonSerializer.Deserialize<AssignmentAnalyticsSettings>(normalizedJson, AssignmentApiSerializationService.JsonOptions()) ?? Default();
        }
        catch
        {
            return Default();
        }
    }

    internal static object ToPublicDto(AssignmentAnalyticsSettings s) => new
    {
        mode = s.Mode,
        s.TrackOpen,
        s.TrackAttempts,
        s.TrackTime,
        s.TrackLanguage,
        s.TrackErrors,
        s.TrackEditorChanges,
        s.TrackClipboard,
        s.TrackFocus,
        s.TrackVisibility,
        s.TrackFullscreen,
        s.TrackCodeSnapshots,
        s.TrackRiskScore,
        s.TrackLiveActivity,
        s.TrackSimilarity,
        s.StoreFullCode,
        s.StorePasteText,
        s.StoreTextSamples,
        s.PasteSampleLimit,
        s.CodeSampleLimit,
        s.CodeSnapshotIntervalSeconds,
        s.EventBatchIntervalSeconds,
        s.RetentionDays,
        s.MaxFullCodeLength,
        s.MaxEventsPerBatch
    };

    internal static bool AllowsEvent(AssignmentAnalyticsSettings s, string? eventType)
    {
        if (string.Equals(s.Mode, "off", StringComparison.OrdinalIgnoreCase)) return false;
        var t = NormalizeEventType(eventType);
        if (t.Length == 0) return false;
        if (t is "assignment_opened" or "assignment_closed" or "page_unloaded" or "heartbeat") return s.TrackOpen || s.TrackTime;
        if (t is "submit_started" or "submit_finished" or "submit_failed" or "submit_passed" or "test_started" or "test_finished" or "math_started" or "math_finished" or "image_trial_started" or "image_trial_finished" or "image_submit_started" or "image_submit_finished") return s.TrackAttempts;
        if (t is "test_answers_changed" or "test_answers_final" or "math_answers_changed" or "math_answers_final") return s.TrackAttempts || s.TrackEditorChanges;
        if (t is "language_changed") return s.TrackLanguage;
        if (t is "runner_error" or "compile_error" or "runtime_error" or "validation_error" or "policy_error") return s.TrackErrors;
        if (t is "code_changed" or "code_changed_aggregate" or "editor_focused" or "editor_blurred" or "draft_restored" or "draft_saved") return s.TrackEditorChanges || s.TrackCodeSnapshots;
        if (t is "copy" or "paste" or "cut" or "selection_changed") return s.TrackClipboard;
        if (t is "window_blur" or "window_focus") return s.TrackFocus;
        if (t is "visibility_hidden" or "visibility_visible") return s.TrackVisibility;
        if (t is "fullscreen_enter" or "fullscreen_exit" or "resize") return s.TrackFullscreen;
        return s.TrackAttempts || s.TrackTime;
    }

    internal static (int Points, string? Reason) RiskFor(AssignmentAnalyticsSettings s, string? eventType, int? codeDelta, int? textLength, int? codeLength = null, long? activeDurationMs = null)
    {
        if (!s.TrackRiskScore) return (0, null);
        var t = NormalizeEventType(eventType);
        var points = 0;
        var reasons = new List<string>();

        void Add(int value, string reason) { points += value; reasons.Add(reason); }

        if (t == "visibility_hidden") Add(15, "вкладка скрыта");
        if (t == "window_blur") Add(10, "окно потеряло фокус");
        if (t == "fullscreen_exit") Add(20, "выход из fullscreen");
        if (t == "copy" || t == "cut") Add(10, t == "copy" ? "copy" : "cut");
        if (t == "paste")
        {
            var len = textLength ?? 0;
            if (len >= 2000) Add(45, $"очень большая вставка {len} символов");
            else if (len >= 1000) Add(35, $"большая вставка {len} символов");
            else if (len >= 300) Add(20, $"вставка {len} символов");
            else Add(5, "paste");
        }
        if ((t == "code_changed" || t == "code_changed_aggregate") && global::System.Math.Abs(codeDelta ?? 0) >= 500) Add(20, $"резкий скачок кода {codeDelta:+#;-#;0}");
        if ((t == "code_changed" || t == "code_changed_aggregate") && global::System.Math.Abs(codeDelta ?? 0) >= 1500) Add(20, $"аномальный скачок кода {codeDelta:+#;-#;0}");
        if ((t == "submit_started" || t == "submit_finished" || t == "submit_passed") && (codeLength ?? textLength ?? 0) >= 1000 && activeDurationMs is > 0 and < 30000) Add(25, "большое решение отправлено быстрее 30 секунд активного времени");
        if (t == "submit_started" && (textLength ?? codeLength ?? 0) >= 1000) Add(10, "отправка большого решения");
        if (t == "page_unloaded") Add(3, "выход со страницы");

        return (global::System.Math.Clamp(points, 0, 100), reasons.Count == 0 ? null : string.Join("; ", reasons));
    }

    internal static string RiskLevel(int score) => score switch
    {
        >= 75 => "critical",
        >= 50 => "high",
        >= 25 => "medium",
        _ => "low"
    };

    internal static string NormalizeEventType(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    internal static string ToJson(AssignmentAnalyticsSettings settings) => JsonSerializer.Serialize(settings, AssignmentApiSerializationService.JsonOptions());

    private static string NormalizeMode(string? value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "none" or "disabled" or "disable" => "off",
            "extended" or "solutions" => "solution",
            "olympiad" or "anti-cheat" or "anticheat" or "exam" => "proctoring",
            "off" or "basic" or "solution" or "proctoring" or "custom" => s,
            _ => "basic"
        };
    }

    private static int Clamp(int value, int min, int max) => global::System.Math.Min(max, global::System.Math.Max(min, value));

    private static bool ReadBool(JsonElement source, string name, bool fallback)
    {
        if (!source.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind is JsonValueKind.True or JsonValueKind.False) return p.GetBoolean();
        if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
        return fallback;
    }

    private static int ReadInt(JsonElement source, string name, int fallback)
    {
        if (!source.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out i)) return i;
        return fallback;
    }

    private static string? ReadString(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        }
        return null;
    }
}
