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
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Common;

internal static class AssignmentApiCommonService
{
    internal static IResult? CheckUserRateLimit(HttpContext http, string bucket)
    {
        var userId = http.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User?.FindFirstValue("sub") ?? "anonymous";
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"{bucket}:{userId}:{ip}";
        if (TaskForgeApiRateLimiters.Allow(bucket, key)) return null;
        return Microsoft.AspNetCore.Http.Results.Json(new { message = "Слишком много запросов. Подождите немного и попробуйте снова.", code = "RATE_LIMITED" }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    internal static string? NodeString(JsonObject o, string name) => o.TryGetPropertyValue(name, out var n) && n is not null ? n.ToString() : null;

    internal static int NodeInt(JsonObject o, string name, int fallback) => int.TryParse(NodeString(o, name), out var v) ? v : fallback;

    internal static bool NodeBool(JsonObject o, string name) => bool.TryParse(NodeString(o, name), out var v) && v;

    internal static string? StripDataUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = s.IndexOf(',');
            if (comma >= 0) s = s[(comma + 1)..];
        }
        return s;
    }

    internal static bool StdoutMatches(string actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        static string Norm(string v) => string.Join("\n", (v ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(x => x.TrimEnd())).Trim();
        return string.Equals(Norm(actual), Norm(expected), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<IResult?> AnalyzeCodePolicyForAssignment(Assignment assignment, string language, string code, IHttpClientFactory clients, IConfiguration cfg, string stage)
    {
        if (!cfg.GetValue("CodeAnalyzer:Enabled", true)) return null;
        var baseUrl = (cfg["CodeAnalyzer:Url"] ?? "http://code-analyzer:8080").TrimEnd('/');
        var forbidden = ParseStringArrayJson(assignment.CodeForbiddenCallsJson);
        var required = ParseStringArrayJson(assignment.CodeRequiredCallsJson);
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(System.Math.Clamp(cfg.GetValue("CodeAnalyzer:TimeoutSeconds", 8), 2, 60));

        try
        {
            using var response = await client.PostAsJsonAsync($"{baseUrl}/analyze", new AnalyzerRequest(NormalizeLanguage(language) ?? language, code, null, forbidden.Length > 0 ? forbidden : null, required.Length > 0 ? required : null), JsonOptions());
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return Problem(503, "CODE_ANALYZER_FAILED", stage, "Сервис анализа кода временно недоступен.");
            }
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var root = doc.RootElement.Clone();
            var ok = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var okProp)
                && okProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                && okProp.GetBoolean();
            if (ok) return null;
            var message = BuildImagePolicyMessage(root);
            return Microsoft.AspNetCore.Http.Results.Json(new { status = 400, code = "CODE_POLICY_FAILED", stage, message, severity = "warning" }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            return Problem(503, "CODE_ANALYZER_FAILED", stage, "Сервис анализа кода временно недоступен.", ex.Message);
        }
    }

    internal static string? ReferenceUrl(JsonObject root)
    {
        var key = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey") ?? NodeString(root, "imageKey");
        if (!string.IsNullOrWhiteSpace(key)) return PrivateFileUrl(key);

        // Legacy fallback only. New image-test v2 stores expected images in MinIO and keeps only keys in TestsJson.
        var base64 = NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64");
        if (!string.IsNullOrWhiteSpace(base64))
        {
            var contentType = NodeString(root, "referenceContentType") ?? NodeString(root, "expectedImageContentType") ?? "image/png";
            return base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? base64 : $"data:{contentType};base64,{base64}";
        }
        return null;
    }

    internal static string ServiceUrl(IConfiguration cfg, string name, string fallback)
    {
        return (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');
    }

    internal static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    internal static bool TextAccepted(string? value, List<string> accepted, bool caseSensitive = false, bool trim = true, double? tolerance = null)
    {
        var v = value ?? string.Empty;
        if (trim) v = v.Trim();
        if (tolerance.HasValue && double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv))
        {
            return accepted.Any(x => double.TryParse((trim ? x.Trim() : x).Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var da) && System.Math.Abs(dv - da) <= tolerance.Value);
        }
        return accepted.Any(x => string.Equals(v, trim ? x.Trim() : x, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }

    internal static bool SetEq(IEnumerable<string> a, IEnumerable<string> b) => a.Select(x => x.Trim()).Where(x => x.Length > 0).OrderBy(x => x).SequenceEqual(b.Select(x => x.Trim()).Where(x => x.Length > 0).OrderBy(x => x), StringComparer.OrdinalIgnoreCase);

    internal static bool SeqEq(IEnumerable<string> a, IEnumerable<string> b) => a.Select(x => x.Trim()).SequenceEqual(b.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

    internal static bool MatchEq(IEnumerable<MatchPair> a, IEnumerable<MatchPair> b) => SetEq(a.Select(x => $"{x.LeftKey}->{x.RightKey}"), b.Select(x => $"{x.LeftKey}->{x.RightKey}"));

    internal static bool IsTimeExpired(TaskAttempt a) => a.TimeLimitSeconds.HasValue && DateTimeOffset.UtcNow > a.StartedAt.AddSeconds(a.TimeLimitSeconds.Value + 5);

    internal static int? TimeLimitFor(List<int?> limits, int attemptNumber) => attemptNumber >= 1 && attemptNumber <= limits.Count && limits[attemptNumber - 1].GetValueOrDefault() > 0 ? limits[attemptNumber - 1] : null;

    internal static List<Guid> OrderedIds(IEnumerable<Guid> ids, bool shuffle, Guid seed) { var list = ids.ToList(); if (shuffle) Shuffle(list, seed); return list; }

    internal static void Shuffle<T>(IList<T> list, Guid seed) { var rnd = new Random(BitConverter.ToInt32(seed.ToByteArray(), 0)); for (var i = list.Count - 1; i > 0; i--) { var j = rnd.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); } }


    internal static async Task MarkRatingDirtyInSolutionsAsync(IHttpClientFactory httpFactory, IConfiguration cfg, IEnumerable<Guid> userIds, string reason, Guid? assignmentId, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(5000).ToArray();
        if (ids.Length == 0) return;
        await PostInternalAsync<object>(
            httpFactory,
            cfg,
            ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080"),
            "/api/internal/rating/dirty-users",
            new { userIds = ids, reason, assignmentId },
            ct);
    }

    internal static async Task MarkAssignmentRatingDirtyInSolutionsAsync(IHttpClientFactory httpFactory, IConfiguration cfg, Guid assignmentId, IEnumerable<Guid> taskUserIds, string reason, CancellationToken ct)
    {
        if (assignmentId == Guid.Empty) return;
        var ids = taskUserIds.Where(x => x != Guid.Empty).Distinct().Take(5000).ToArray();
        await PostInternalAsync<object>(
            httpFactory,
            cfg,
            ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080"),
            $"/api/internal/rating/assignments/{assignmentId:D}/dirty-users",
            new { userIds = ids, reason, assignmentId },
            ct);
    }

    internal static async Task<T?> PostInternalAsync<T>(IHttpClientFactory httpFactory, IConfiguration cfg, string baseUrl, string path, object payload, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + path)
            {
                Content = JsonContent.Create(payload, options: JsonOptions())
            };
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

    internal static Guid? RequireUser(HttpContext http, IConfiguration cfg) => TaskForgeRequestSecurity.UserId(http, cfg);

    internal static IResult Unauthorized() => Microsoft.AspNetCore.Http.Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Microsoft.AspNetCore.Http.Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);

    internal static int? IntProp(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : null;

    internal static bool BoolProp(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    internal static string? StringProp(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) ? v.ToString() : null;

    internal static string Clean(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

    internal static JsonElement WrapSpec(JsonElement source, string settingsName, string arrayName)
    {
        var node = new JsonObject();
        if (source.TryGetProperty(settingsName, out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            node[settingsName] = JsonNode.Parse(settings.GetRawText());
        }
        if (source.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            node[arrayName] = JsonNode.Parse(arr.GetRawText());
        }
        return JsonSerializer.SerializeToElement(node, JsonOptions());
    }

    internal static JsonElement WrapInteractiveSpec(JsonElement source, string arrayName, bool isMath)
    {
        var node = new JsonObject();
        var settings = new JsonObject();

        foreach (var settingsName in isMath
                     ? new[] { "testSettings", "settings", "mathSettings", "attemptSettings" }
                     : new[] { "testSettings", "settings", "quizSettings", "attemptSettings" })
        {
            if (TryGetPropertyLoose(source, settingsName, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in nested.EnumerateObject())
                {
                    settings[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                }
            }
        }

        CopyLoose(source, settings, "maxAttempts", "maxAttempts", "attempts", "attemptLimit", "maxAttemptCount");
        CopyLoose(source, settings, "passPercent", "passPercent", "passingPercent", "passScore", "successPercent");
        CopyLoose(source, settings, "allowReview", "allowReview", "showReview", "reviewAllowed", "showResults");
        CopyLoose(source, settings, "attemptTimeLimitsSeconds", "attemptTimeLimitsSeconds", "timeLimits", "timeLimitSecondsByAttempt");

        if (isMath)
        {
            CopyLoose(source, settings, "shuffleBlocks", "shuffleBlocks", "randomizeBlocks", "randomBlocks", "blocksRandomOrder");
        }
        else
        {
            CopyLoose(source, settings, "shuffleQuestions", "shuffleQuestions", "randomizeQuestions", "randomQuestions", "questionsRandomOrder");
            CopyLoose(source, settings, "shuffleAnswers", "shuffleAnswers", "randomizeAnswers", "randomAnswers", "answersRandomOrder", "shuffleOptions", "randomizeOptions");
        }

        if (settings.Count > 0) node["settings"] = settings;
        if (TryGetPropertyLoose(source, arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            node[arrayName] = JsonNode.Parse(arr.GetRawText());
        }
        return JsonSerializer.SerializeToElement(node, JsonOptions());
    }

    internal static bool TryGetPropertyLoose(JsonElement source, string name, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            if (source.TryGetProperty(name, out value)) return true;
            foreach (var prop in source.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    internal static void CopyLoose(JsonElement source, JsonObject target, string targetName, params string[] sourceNames)
    {
        foreach (var sourceName in sourceNames)
        {
            if (!TryGetPropertyLoose(source, sourceName, out var value)) continue;
            target[targetName] = JsonNode.Parse(value.GetRawText());
            return;
        }
    }

    internal static string? FirstString(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyLoose(source, name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
            if (name == "tags" && v.ValueKind == JsonValueKind.Array)
            {
                var tags = v.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => x.Length > 0).ToArray();
                return tags.Length == 0 ? null : string.Join(", ", tags);
            }
        }
        return null;
    }

    internal static Guid? FirstGuid(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyLoose(source, name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) && g != Guid.Empty) return g;
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var nested) && nested.ValueKind == JsonValueKind.String && Guid.TryParse(nested.GetString(), out g) && g != Guid.Empty) return g;
        }
        return null;
    }

    internal static int? FirstInt(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyLoose(source, name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
        }
        return null;
    }

    internal static bool? FirstBool(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyLoose(source, name, out var v)) continue;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
        }
        return null;
    }

    internal static JsonElement? FirstElement(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyLoose(source, name, out var v)) return v;
        }
        return null;
    }

    internal static List<string>? FirstStringList(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyLoose(source, name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Array)
            {
                var list = v.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return list.Count == 0 ? null : list;
            }
            if (v.ValueKind == JsonValueKind.String)
            {
                var list = (v.GetString() ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return list.Count == 0 ? null : list;
            }
        }
        return null;
    }

    internal static string[] SupportedCodeLanguages() => ["cpp", "python", "csharp", "javascript", "pascal", "java"];

    internal static Guid GuidProp(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && Guid.TryParse(p.ToString(), out var id) ? id : Guid.Empty;

    internal static List<TestAnswer> AnswersArray(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(x => new TestAnswer(GuidProp(x, "questionId"), x.TryGetProperty("selectedOptionKey", out var s) ? s.ToString() : null, Strings(x, "selectedOptionKeys"), x.TryGetProperty("text", out var t) ? t.ToString() : null)).ToList();
    }

    internal static List<string> Strings(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : [];

    internal static List<MatchPair> MatchPairs(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => new MatchPair(x.TryGetProperty("leftKey", out var l) ? l.ToString() : string.Empty, x.TryGetProperty("rightKey", out var r) ? r.ToString() : string.Empty)).ToList() : [];

    internal static int Int(JsonObject o, string n, int d) => o[n] is JsonValue v && int.TryParse(v.ToString(), out var x) ? x : d;

    internal static bool Bool(JsonObject o, string n, bool d) => o[n] is JsonValue v && bool.TryParse(v.ToString(), out var x) ? x : d;

    internal static string Str(JsonObject o, string n, string d) => o[n]?.GetValue<string>() ?? d;

    internal static Guid GuidV(JsonObject o, string n) => Guid.TryParse(o[n]?.ToString(), out var id) && id != Guid.Empty ? id : Guid.NewGuid();

    internal static double? DoubleN(JsonObject o, string n) => o[n] is JsonValue v && double.TryParse(v.ToString().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : null;

    internal static List<string> StringList(JsonObject o, string n) => o[n] is JsonArray a ? a.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToList() : [];

    internal static List<int?> IntList(JsonObject o, string n) => o[n] is JsonArray a ? a.Select(x => int.TryParse(x?.ToString(), out var v) && v > 0 ? (int?)v : null).ToList() : [];

    internal static List<Option> Options(JsonObject o, string n = "options") => o[n] is JsonArray a ? a.OfType<JsonObject>().Select(x => new Option(Str(x, "key", Guid.NewGuid().ToString("N")[..4]), Str(x, "text", ""))).ToList() : [];

    internal static List<MatchPair> MatchPairList(JsonObject o, string n) => o[n] is JsonArray a ? a.OfType<JsonObject>().Select(x => new MatchPair(Str(x, "leftKey", ""), Str(x, "rightKey", ""))).ToList() : [];

    internal static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');

    internal static double Percent(int num, int den) => den <= 0 ? 0 : System.Math.Round(num * 100.0 / den, 1);

}
