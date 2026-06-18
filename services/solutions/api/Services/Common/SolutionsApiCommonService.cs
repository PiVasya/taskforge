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
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Services.Common;

internal static class SolutionsApiCommonService
{
    internal static IResult? CheckUserRateLimit(HttpContext http, string bucket)
    {
        var userId = http.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User?.FindFirstValue("sub") ?? "anonymous";
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"{bucket}:{userId}:{ip}";
        if (TaskForgeApiRateLimiters.Allow(bucket, key)) return null;
        return Microsoft.AspNetCore.Http.Results.Json(new { message = "Слишком много запросов. Подождите немного и попробуйте снова.", code = "RATE_LIMITED" }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    internal static async Task<EnqueueResult> EnqueueExecutionJobAsync(Guid submissionId, Guid assignmentId, Guid userId, string language, string code, string? input, JsonElement[] tests, JudgeSpec? spec, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var baseUrl = ServiceUrl(cfg, "ExecutionApi", "http://execution-api:8080");
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(System.Math.Clamp(cfg.GetValue("Judge:EnqueueTimeoutSeconds", 10), 2, 60));

        var payload = new CreateExecutionJobRequest(
            submissionId,
            assignmentId,
            userId,
            language,
            code,
            input,
            tests,
            null,
            null,
            null,
            spec?.CodeForbiddenCalls,
            spec?.CodeRequiredCalls);

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/execution/jobs")
        {
            Content = JsonContent.Create(payload, options: JsonOptions())
        };
        AddInternalKey(msg, cfg);

        try
        {
            using var resp = await client.SendAsync(msg, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new EnqueueResult(false, null, $"Execution API не принял задачу проверки: {(int)resp.StatusCode}.", CloneJson(text));
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement.Clone();
            var jobId = TryReadGuid(root, "id") ?? (root.TryGetProperty("job", out var job) ? TryReadGuid(job, "id") : null);
            return new EnqueueResult(true, jobId, "Задача проверки создана.", root);
        }
        catch (Exception ex)
        {
            return new EnqueueResult(false, null, "Execution pipeline временно недоступен.", CloneJson(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions())));
        }
    }

    internal static async Task<SolutionSubmission?> WaitForTerminalSubmissionAsync(SolutionsDbContext db, Guid submissionId, IConfiguration cfg, CancellationToken ct)
    {
        var timeoutMs = System.Math.Clamp(cfg.GetValue("Judge:SubmitWaitMilliseconds", 18000), 0, 60000);
        if (timeoutMs <= 0) return null;

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var row = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == submissionId, ct);
            if (row == null) return null;
            if (IsTerminalVerdict(row.Status))
            {
                return row;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == submissionId, ct);
    }

    internal static string[] EffectiveAllowedLanguages(JudgeSpec spec)
    {
        var values = spec.AllowedLanguages ?? [];
        var list = values.Select(NormalizeLanguage).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (list.Length > 0) return list;
        var fallback = NormalizeLanguage(spec.Language) ?? "csharp";
        return [fallback];
    }

    internal static bool IsAllowedLanguage(string language, JudgeSpec spec)
        => EffectiveAllowedLanguages(spec).Contains(language, StringComparer.OrdinalIgnoreCase);

    internal static bool IsPassedResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty("passed", out var passed) && passed.ValueKind is JsonValueKind.True or JsonValueKind.False) return passed.GetBoolean();
        if (item.TryGetProperty("status", out var status))
        {
            var value = NormalizeStatusKey(status.ToString());
            return value is "accepted" or "passed" or "success";
        }
        return false;
    }

    internal static bool IsCompileErrorResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty("status", out var status) && string.Equals(status.ToString(), "compile_error", StringComparison.OrdinalIgnoreCase)) return true;
        if (item.TryGetProperty("compileStderr", out var compileStderr) && !string.IsNullOrWhiteSpace(compileStderr.ToString())) return true;
        return false;
    }

    internal static bool IsCompileErrorRoot(JsonElement root)
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

    internal static string ServiceUrl(IConfiguration cfg, string name, string fallback)
    {
        return (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');
    }

    internal static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
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

    internal static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');

    internal static string UserSummaryHaystack(UserSummaryDto? user, Guid id) => NormalizeSearch($"{id} {user?.Login} {user?.MaskedEmail} {user?.DisplayName} {user?.FirstName} {user?.LastName}");

    internal static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = System.Math.Min(System.Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    internal static async Task<QuotaView> StatusFor(SolutionsDbContext db, Guid userId, string bucket, int capacity, TimeSpan interval)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.UserQuotaBuckets.FirstOrDefaultAsync(x => x.UserId == userId && x.BucketType == bucket);
        if (row == null)
        {
            row = new UserQuotaBucket { UserId = userId, BucketType = bucket, Tokens = capacity, LastRefillAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.UserQuotaBuckets.Add(row);
            await db.SaveChangesAsync();
        }
        var elapsed = now - row.LastRefillAtUtc;
        if (elapsed.TotalSeconds >= interval.TotalSeconds)
        {
            var refill = (int)System.Math.Floor(elapsed.TotalSeconds / interval.TotalSeconds);
            row.Tokens = System.Math.Min(capacity, row.Tokens + refill);
            row.LastRefillAtUtc = row.LastRefillAtUtc.AddSeconds(refill * interval.TotalSeconds);
            row.UpdatedAtUtc = now;
            await db.SaveChangesAsync();
        }
        var next = row.LastRefillAtUtc.Add(interval);
        return new QuotaView(bucket, System.Math.Max(0, row.Tokens), capacity, row.Tokens >= capacity ? 0 : System.Math.Max(1, (int)System.Math.Ceiling((next - now).TotalSeconds)), next, row.Tokens > 0);
    }

    internal static IResult Unauthorized() => Microsoft.AspNetCore.Http.Results.Json(new { message = "Сессия истекла или вы не вошли в систему.", code = "AUTH_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Microsoft.AspNetCore.Http.Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);

    internal static void RemoveReferenceFields(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr) RemoveReferenceFields(item);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var key in new[] { "referenceUrl", "ReferenceUrl", "expectedUrl", "ExpectedUrl", "expectedImageUrl", "expectedImageKey", "referenceKey", "imageTestReferenceKey", "expectedImageBase64", "referenceBase64", "imageBase64" })
        {
            obj.Remove(key);
        }
        foreach (var item in obj.ToList()) RemoveReferenceFields(item.Value);
    }

}
