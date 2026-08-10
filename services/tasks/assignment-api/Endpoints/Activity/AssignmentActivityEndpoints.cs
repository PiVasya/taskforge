using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Hubs;
using TaskForge.Tasks.Api.Services.Analytics;
using TaskForge.Tasks.Api.Services.Access;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapAssignmentActivityEndpoints(WebApplication app)
    {
        app.MapPost("/api/assignments/{assignmentId:guid}/activity/batch", async (Guid assignmentId, AssignmentActivityBatchRequest request, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, IHubContext<AssignmentAnalyticsHub> hub, CancellationToken ct) =>
        {
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!userId.HasValue) return Unauthorized();

            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            if (!await AssignmentApiAccessService.CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct))
            {
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            }

            var settings = AssignmentAnalyticsSettingsService.FromJson(assignment.AnalyticsSettingsJson);
            var incoming = (request.Events ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.EventType))
                .Take(settings.MaxEventsPerBatch)
                .ToList();

            if (incoming.Count == 0 || string.Equals(settings.Mode, "off", StringComparison.OrdinalIgnoreCase))
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new { accepted = true, stored = 0 });
            }

            var sessionId = CleanSessionId(request.SessionId);
            var ipHash = HashOrNull(http.Connection.RemoteIpAddress?.ToString());
            var userAgentHash = HashOrNull(http.Request.Headers.UserAgent.ToString());
            var now = DateTimeOffset.UtcNow;
            var sessionEventUids = incoming
                .Select(x => CleanEventUid(x.EventUid))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var existingEventUids = sessionEventUids.Length == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (await db.AssignmentActivityEvents.AsNoTracking()
                    .Where(x => x.AssignmentId == assignmentId && x.UserId == userId.Value && x.SessionId == sessionId && x.EventUid != null && sessionEventUids.Contains(x.EventUid))
                    .Select(x => x.EventUid!)
                    .ToListAsync(ct))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var events = new List<AssignmentActivityEvent>();
            var codePayloadByEvent = new Dictionary<Guid, (string? CodeSample, string? FullCode)>();

            foreach (var e in incoming)
            {
                var eventType = AssignmentAnalyticsSettingsService.NormalizeEventType(e.EventType);
                if (!AssignmentAnalyticsSettingsService.AllowsEvent(settings, eventType)) continue;

                var eventUid = CleanEventUid(e.EventUid);
                if (!string.IsNullOrWhiteSpace(eventUid) && existingEventUids.Contains(eventUid)) continue;

                var codeSample = settings.StoreTextSamples || settings.TrackCodeSnapshots
                    ? ClampText(e.CodeSample, settings.CodeSampleLimit)
                    : null;
                var fullCode = settings.StoreFullCode ? ClampText(e.FullCode, settings.MaxFullCodeLength) : null;
                var textLength = e.TextLength ?? SafeLen(e.TextSample);
                var textSample = ShouldStoreTextSample(settings, eventType)
                    ? ClampText(e.TextSample, settings.PasteSampleLimit)
                    : null;
                var payloadJson = NormalizePayload(e.Payload);
                var risk = AssignmentAnalyticsSettingsService.RiskFor(settings, eventType, e.CodeDelta, textLength, e.CodeLength, e.ActiveDurationMs);

                var row = new AssignmentActivityEvent
                {
                    Id = Guid.NewGuid(),
                    AssignmentId = assignmentId,
                    UserId = userId.Value,
                    SessionId = sessionId,
                    EventUid = eventUid,
                    Sequence = global::System.Math.Max(0, e.Sequence ?? 0),
                    EventType = eventType,
                    CreatedAt = now,
                    ClientTime = e.ClientTime,
                    PayloadJson = payloadJson,
                    CodeLength = NonNegative(e.CodeLength),
                    CodeDelta = e.CodeDelta,
                    CodeHash = ClampText(e.CodeHash, 128),
                    TextLength = NonNegative(textLength),
                    TextHash = ClampText(e.TextHash, 128),
                    TextSample = textSample,
                    Language = ClampText(e.Language, 40),
                    ActiveDurationMs = NonNegativeLong(e.ActiveDurationMs),
                    HiddenDurationMs = NonNegativeLong(e.HiddenDurationMs),
                    BlurDurationMs = NonNegativeLong(e.BlurDurationMs),
                    AttemptId = EmptyToNull(e.AttemptId),
                    SubmissionId = EmptyToNull(e.SubmissionId),
                    RiskPoints = risk.Points,
                    RiskReason = risk.Reason,
                    IpHash = ipHash,
                    UserAgentHash = userAgentHash
                };
                events.Add(row);
                if (!string.IsNullOrWhiteSpace(eventUid)) existingEventUids.Add(eventUid);
                if (!string.IsNullOrWhiteSpace(codeSample) || !string.IsNullOrWhiteSpace(fullCode))
                {
                    codePayloadByEvent[row.Id] = (codeSample, fullCode);
                }
            }

            if (events.Count > 0)
            {
                db.AssignmentActivityEvents.AddRange(events);
                var snapshots = AssignmentActivityAggregationService.BuildSnapshots(settings, events, codePayloadByEvent);
                if (snapshots.Count > 0) db.AssignmentCodeSnapshots.AddRange(snapshots);
                await AssignmentActivityAggregationService.UpsertSessionsAsync(db, events, ct);
                await db.SaveChangesAsync(ct);
                await MaybeCleanupOldAnalyticsAsync(db, assignmentId, settings.RetentionDays, ct);

                if (settings.TrackLiveActivity || string.Equals(settings.Mode, "proctoring", StringComparison.OrdinalIgnoreCase))
                {
                    var live = events
                        .Where(x => x.RiskPoints > 0 || x.EventType is "submit_started" or "submit_finished" or "submit_failed" or "paste" or "window_blur" or "visibility_hidden" or "fullscreen_exit")
                        .TakeLast(20)
                        .Select(AssignmentActivityAggregationService.ToLiveDto)
                        .ToList();
                    if (live.Count > 0)
                    {
                        await hub.Clients.Group(AssignmentAnalyticsHubGroups.ForAssignment(assignmentId)).SendAsync("assignmentActivity", live, ct);
                    }
                }
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new { accepted = true, stored = events.Count });
        });

        app.MapGet("/api/admin/assignments/{assignmentId:guid}/activity", async (Guid assignmentId, TasksDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AssignmentActivityEvents.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId)
                .OrderByDescending(x => x.CreatedAt)
                .Take(500)
                .Select(x => new
                {
                    x.Id,
                    x.AssignmentId,
                    x.UserId,
                    x.SessionId,
                    x.EventType,
                    x.CreatedAt,
                    x.ClientTime,
                    x.CodeLength,
                    x.CodeDelta,
                    x.TextLength,
                    x.TextSample,
                    x.Language,
                    x.ActiveDurationMs,
                    x.HiddenDurationMs,
                    x.BlurDurationMs,
                    x.AttemptId,
                    x.SubmissionId,
                    x.RiskPoints,
                    x.RiskReason
                })
                .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        app.MapGet("/api/admin/assignments/{assignmentId:guid}/sessions", async (Guid assignmentId, TasksDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AssignmentWorkSessions.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId)
                .OrderByDescending(x => x.RiskScore)
                .ThenByDescending(x => x.LastActivityAt)
                .Take(300)
                .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        app.MapGet("/api/admin/assignments/{assignmentId:guid}/users/{userId:guid}/timeline", async (Guid assignmentId, Guid userId, TasksDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AssignmentActivityEvents.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId && x.UserId == userId)
                .OrderBy(x => x.CreatedAt)
                .Take(1000)
                .Select(x => new
                {
                    x.Id,
                    x.SessionId,
                    x.EventType,
                    x.CreatedAt,
                    x.ClientTime,
                    x.CodeLength,
                    x.CodeDelta,
                    x.TextLength,
                    x.TextSample,
                    x.Language,
                    x.ActiveDurationMs,
                    x.HiddenDurationMs,
                    x.BlurDurationMs,
                    x.RiskPoints,
                    x.RiskReason
                })
                .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        return app;
    }

    private static async Task MaybeCleanupOldAnalyticsAsync(TasksDbContext db, Guid assignmentId, int retentionDays, CancellationToken ct)
    {
        if (retentionDays <= 0) return;
        // Cheap probabilistic cleanup: no background worker required, no extra migration required.
        if (Random.Shared.Next(0, 25) != 0) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        await db.AssignmentActivityEvents.Where(x => x.AssignmentId == assignmentId && x.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.AssignmentCodeSnapshots.Where(x => x.AssignmentId == assignmentId && x.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.AssignmentWorkSessions.Where(x => x.AssignmentId == assignmentId && x.LastActivityAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string CleanSessionId(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0) return Guid.NewGuid().ToString("N");
        var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':').Take(80).ToArray());
        return safe.Length == 0 ? Guid.NewGuid().ToString("N") : safe;
    }

    private static string? CleanEventUid(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0) return null;
        var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.').Take(120).ToArray());
        return safe.Length == 0 ? null : safe;
    }

    private static string? NormalizePayload(JsonElement? payload)
    {
        if (!payload.HasValue || payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var raw = payload.Value.GetRawText();
        if (raw.Length > 4000) raw = raw[..4000];
        try
        {
            using var _ = JsonDocument.Parse(raw);
            return raw;
        }
        catch
        {
            return JsonSerializer.Serialize(new { raw }, JsonOptions());
        }
    }

    private static bool ShouldStoreTextSample(AssignmentAnalyticsSettings settings, string eventType)
    {
        if (!settings.StoreTextSamples) return false;
        if (eventType is "paste" or "cut" or "copy") return settings.StorePasteText && settings.PasteSampleLimit > 0;
        if (eventType is "test_answers_changed" or "test_answers_final" or "math_answers_changed" or "math_answers_final") return settings.PasteSampleLimit > 0;
        return false;
    }

    private static string? ClampText(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0) return null;
        return value.Length <= max ? value : value[..max];
    }

    private static int? NonNegative(int? value) => value.HasValue ? global::System.Math.Max(0, value.Value) : null;
    private static long? NonNegativeLong(long? value) => value.HasValue ? global::System.Math.Max(0L, value.Value) : null;

    private static int SafeLen(string? value) => string.IsNullOrEmpty(value) ? 0 : value.Length;

    private static Guid? EmptyToNull(Guid? value) => value.HasValue && value.Value != Guid.Empty ? value : null;

    private static string? HashOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
