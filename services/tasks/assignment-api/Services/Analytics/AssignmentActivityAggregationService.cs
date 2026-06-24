using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Serialization;

namespace TaskForge.Tasks.Api.Services.Analytics;

internal static class AssignmentActivityAggregationService
{
    internal static async Task<List<AssignmentWorkSession>> UpsertSessionsAsync(TasksDbContext db, IEnumerable<AssignmentActivityEvent> newEvents, CancellationToken ct)
    {
        var groups = newEvents
            .Where(x => x.AssignmentId != Guid.Empty && x.UserId != Guid.Empty && !string.IsNullOrWhiteSpace(x.SessionId))
            .GroupBy(x => new { x.AssignmentId, x.UserId, x.SessionId })
            .ToList();
        if (groups.Count == 0) return new List<AssignmentWorkSession>();

        var assignmentIds = groups.Select(x => x.Key.AssignmentId).Distinct().ToArray();
        var userIds = groups.Select(x => x.Key.UserId).Distinct().ToArray();
        var sessionIds = groups.Select(x => x.Key.SessionId).Distinct().ToArray();
        var existing = await db.AssignmentWorkSessions
            .Where(x => assignmentIds.Contains(x.AssignmentId) && userIds.Contains(x.UserId) && sessionIds.Contains(x.SessionId))
            .ToListAsync(ct);

        var touched = new List<AssignmentWorkSession>();
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(x => x.ClientTime ?? x.CreatedAt).ThenBy(x => x.CreatedAt).ToList();
            var session = existing.FirstOrDefault(x => x.AssignmentId == group.Key.AssignmentId && x.UserId == group.Key.UserId && x.SessionId == group.Key.SessionId);
            if (session == null)
            {
                session = new AssignmentWorkSession
                {
                    AssignmentId = group.Key.AssignmentId,
                    UserId = group.Key.UserId,
                    SessionId = group.Key.SessionId,
                    StartedAt = ordered.First().ClientTime ?? ordered.First().CreatedAt,
                    LastActivityAt = ordered.Last().ClientTime ?? ordered.Last().CreatedAt
                };
                db.AssignmentWorkSessions.Add(session);
            }

            session.StartedAt = Min(session.StartedAt, ordered.First().ClientTime ?? ordered.First().CreatedAt);
            session.LastActivityAt = Max(session.LastActivityAt, ordered.Last().ClientTime ?? ordered.Last().CreatedAt);
            session.FinishedAt = ordered.Any(IsClosingEvent) ? session.LastActivityAt : session.FinishedAt;
            session.LastEventType = ordered.Last().EventType;

            foreach (var e in ordered)
            {
                ApplyEvent(session, e);
            }

            session.TotalDurationMs = global::System.Math.Max(session.TotalDurationMs, (long)global::System.Math.Max(0, (session.LastActivityAt - session.StartedAt).TotalMilliseconds));
            session.RiskLevel = AssignmentAnalyticsSettingsService.RiskLevel(session.RiskScore);
            touched.Add(session);
        }
        return touched;
    }

    internal static List<AssignmentCodeSnapshot> BuildSnapshots(AssignmentAnalyticsSettings settings, IEnumerable<AssignmentActivityEvent> events, IReadOnlyDictionary<Guid, (string? CodeSample, string? FullCode)> codeByEvent)
    {
        if (!settings.TrackCodeSnapshots) return new List<AssignmentCodeSnapshot>();
        var snapshots = new List<AssignmentCodeSnapshot>();
        foreach (var e in events)
        {
            var source = SnapshotSource(e.EventType);
            if (source == null) continue;
            codeByEvent.TryGetValue(e.Id, out var code);
            var sample = settings.CodeSampleLimit > 0 ? Clamp(code.CodeSample, settings.CodeSampleLimit) : null;
            snapshots.Add(new AssignmentCodeSnapshot
            {
                AssignmentId = e.AssignmentId,
                UserId = e.UserId,
                SessionId = e.SessionId,
                CreatedAt = e.CreatedAt,
                Source = source,
                Language = e.Language,
                CodeLength = global::System.Math.Max(0, e.CodeLength ?? 0),
                CodeDelta = e.CodeDelta,
                CodeHash = e.CodeHash,
                CodeSample = sample,
                FullCode = settings.StoreFullCode ? code.FullCode : null,
                RelatedEventId = e.Id,
                RelatedSubmissionId = e.SubmissionId
            });
        }
        return snapshots;
    }

    internal static string? SnapshotSource(string? eventType)
    {
        var t = AssignmentAnalyticsSettingsService.NormalizeEventType(eventType);
        return t switch
        {
            "paste" => "paste",
            "submit_started" or "submit_finished" or "submit_passed" => "submit",
            "code_changed" or "code_changed_aggregate" => "interval",
            "draft_restored" => "draft",
            _ => null
        };
    }

    internal static object ToLiveDto(AssignmentActivityEvent x) => new
    {
        eventId = x.Id,
        assignmentId = x.AssignmentId,
        userId = x.UserId,
        sessionId = x.SessionId,
        eventType = x.EventType,
        createdAtUtc = x.CreatedAt,
        x.CodeLength,
        x.CodeDelta,
        x.TextLength,
        x.Language,
        x.ActiveDurationMs,
        x.HiddenDurationMs,
        x.BlurDurationMs,
        x.RiskPoints,
        x.RiskReason
    };

    private static void ApplyEvent(AssignmentWorkSession s, AssignmentActivityEvent e)
    {
        var t = AssignmentAnalyticsSettingsService.NormalizeEventType(e.EventType);
        switch (t)
        {
            case "assignment_opened": s.OpenCount++; break;
            case "assignment_closed":
            case "page_unloaded": s.CloseCount++; break;
            case "visibility_hidden": s.HiddenCount++; break;
            case "visibility_visible": s.VisibleCount++; break;
            case "window_blur": s.BlurCount++; break;
            case "window_focus": s.FocusCount++; break;
            case "paste": s.PasteCount++; break;
            case "copy": s.CopyCount++; break;
            case "cut": s.CutCount++; break;
            case "language_changed": s.LanguageChangeCount++; break;
            case "fullscreen_exit": s.FullscreenExitCount++; break;
        }

        if (t is "code_changed" or "code_changed_aggregate") s.CodeChangeCount++;
        if (t is "submit_started" or "image_submit_started" or "test_started" or "math_started") s.SubmitCount++;
        if (t is "submit_failed") s.FailedSubmitCount++;
        if (t is "submit_finished" or "submit_passed" or "image_submit_finished" or "test_finished" or "math_finished")
        {
            if (e.PayloadJson?.Contains("\"passed\":true", StringComparison.OrdinalIgnoreCase) == true) s.PassedSubmitCount++;
        }

        if (e.ActiveDurationMs.HasValue) s.ActiveDurationMs = global::System.Math.Max(s.ActiveDurationMs, global::System.Math.Max(0, e.ActiveDurationMs.Value));
        if (e.HiddenDurationMs.HasValue) s.HiddenDurationMs = global::System.Math.Max(s.HiddenDurationMs, global::System.Math.Max(0, e.HiddenDurationMs.Value));
        if (e.BlurDurationMs.HasValue) s.BlurDurationMs = global::System.Math.Max(s.BlurDurationMs, global::System.Math.Max(0, e.BlurDurationMs.Value));

        if (e.CodeLength.HasValue)
        {
            s.MaxCodeLength = global::System.Math.Max(s.MaxCodeLength, e.CodeLength.Value);
            s.FinalCodeLength = e.CodeLength.Value;
        }
        if (!string.IsNullOrWhiteSpace(e.CodeHash)) s.FinalCodeHash = e.CodeHash;
        if (!string.IsNullOrWhiteSpace(e.Language)) s.LastLanguage = e.Language;
        if (e.RiskPoints > 0)
        {
            s.RiskScore = global::System.Math.Clamp(s.RiskScore + e.RiskPoints, 0, 100);
            s.RiskReasonsJson = MergeReason(s.RiskReasonsJson, e.RiskReason);
        }
    }

    private static string? MergeReason(string? json, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return json;
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                list.AddRange(JsonSerializer.Deserialize<List<string>>(json, AssignmentApiSerializationService.JsonOptions()) ?? new List<string>());
            }
            catch { }
        }
        foreach (var part in reason.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!list.Contains(part, StringComparer.OrdinalIgnoreCase)) list.Add(part);
        }
        return JsonSerializer.Serialize(list.Take(12).ToList(), AssignmentApiSerializationService.JsonOptions());
    }

    private static bool IsClosingEvent(AssignmentActivityEvent e)
    {
        var t = AssignmentAnalyticsSettingsService.NormalizeEventType(e.EventType);
        return t is "assignment_closed" or "page_unloaded";
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
    private static string? Clamp(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0) return null;
        return value.Length <= max ? value : value[..max];
    }
}
