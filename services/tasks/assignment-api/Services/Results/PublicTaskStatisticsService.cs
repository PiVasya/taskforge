
namespace TaskForge.Tasks.Api.Services.Results;

// Public profiles only receive aggregated verdicts. No answers, attempt IDs or
// private task material leave this internal projection.
internal sealed record PublicTaskStatisticsDay(string Day, int Accepted, int Failed, int Pending);
internal sealed record PublicTaskStatisticsKind(string Kind, long Attempts, long Accepted, long Failed, long Pending);
internal sealed record PublicTaskStatistics(
    long ResultAttempts, long ResultAccepted, long ResultFailed, long ResultPending,
    IReadOnlyList<PublicTaskStatisticsDay> DailyResults,
    IReadOnlyList<PublicTaskStatisticsKind> ResultTypes);

internal static class PublicTaskStatisticsService
{
    internal const int HistoryDays = 90;

    internal static string Verdict(bool passed, bool submitted, bool expired)
        => passed && (submitted || expired) ? "accepted"
            : !submitted && !expired ? "pending" : "failed";

    internal static async Task<PublicTaskStatistics> LoadAsync(
        IHttpClientFactory clients, IConfiguration cfg, Guid userId, CancellationToken ct)
    {
        var result = await TaskForge.Tasks.Api.Services.Ownership.InteractiveAttemptClient
            .GetPublicStatisticsAsync(clients, cfg, userId, ct);
        return result ?? throw new HttpRequestException("Solutions returned no public task statistics.");
    }
}
