using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TaskForge.Tasks.Api.Services.Transfer;

internal sealed record TransferProgressSnapshot(string Phase, int Percent, int CompletedItems,
    int TotalItems, long BytesWritten, string? CurrentItem);

internal sealed class GraphTransferProgress(Guid transferId, string kind, ILogger logger)
{
    private static readonly object Key = new();
    private readonly object gate = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private TransferProgressSnapshot snapshot = new("queued", 0, 0, 0, 0, null);
    internal void Attach(HttpContext http) => http.Items[Key] = this;
    internal static GraphTransferProgress? Get(HttpContext http) => http.Items.TryGetValue(Key, out var item) ? item as GraphTransferProgress : null;
    internal TransferProgressSnapshot Read() { lock (gate) return snapshot; }

    private long lastLogged;
    internal void Report(string phase, int percent, int? completed = null, int? total = null, string? current = null, long? bytes = null)
    {
        TransferProgressSnapshot value;
        bool log;
        lock (gate)
        {
            log = phase != snapshot.Phase || elapsed.ElapsedMilliseconds - lastLogged >= 1000;
            snapshot = new(phase, global::System.Math.Max(snapshot.Percent, global::System.Math.Clamp(percent, 0, 99)),
                global::System.Math.Max(0, completed ?? snapshot.CompletedItems), global::System.Math.Max(0, total ?? snapshot.TotalItems),
                bytes ?? snapshot.BytesWritten, current ?? snapshot.CurrentItem);
            value = snapshot;
            if (log) lastLogged = elapsed.ElapsedMilliseconds;
        }
        if (log) logger.LogInformation("TASK_GRAPH_TRANSFER_PHASE Transfer={TransferId} Kind={Kind} Phase={Phase} Percent={Percent} Completed={Completed} Total={Total} Item={Item} ElapsedMs={ElapsedMs}",
            transferId, kind, phase, value.Percent, value.CompletedItems, value.TotalItems, value.CurrentItem, elapsed.ElapsedMilliseconds);
    }

    internal void ImportPhase(string phase, int tasks, int courses)
    {
        var assignment = Regex.Match(phase, @"^apply\.assignment-(\d+)-of-(\d+)$");
        if (assignment.Success)
        {
            var current = int.Parse(assignment.Groups[1].Value);
            var total = int.Parse(assignment.Groups[2].Value);
            Report("assignments", 30 + 40 * (current - 1) / global::System.Math.Max(1, total), current - 1, total);
            return;
        }
        var value = phase switch
        {
            "prepare.begin" => 5,
            "prepare.snapshot-start" or "prepare.layout-only-snapshot-skipped" => 10,
            "prepare.journal-saved" => 15,
            "apply.bindings-checked" => 25,
            "layout-only.assignments-loaded" => 40,
            "apply.assignments-start" => 30,
            "apply.assignments-done" => 72,
            "layout-only.receipt-ready" => 72,
            "audit.done" => 82,
            "transaction.commit-start" => 88,
            "transaction.commit-done" => 92,
            "map.begin" => 10,
            "map.receipt-validate" => 80,
            "map.journal-complete" => 95,
            _ => Read().Percent
        };
        Report(phase, value, phase is "apply.assignments-done" or "layout-only.receipt-ready" ? tasks : null, tasks > 0 ? tasks : null);
    }
}
