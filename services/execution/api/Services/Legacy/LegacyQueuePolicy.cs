using TaskForge.Execution.Api.Domain;

namespace TaskForge.Execution.Api.Services.Legacy;

internal enum LegacyExpirationAction { None, Requeue, Fail }

internal static class LegacyQueuePolicy
{
    internal static LegacyExpirationAction Expiration(
        ExecutionJob job, DateTimeOffset now, int maxAttempts, TimeSpan runningTimeout, TimeSpan queuedTimeout)
    {
        if (job.Kind != ExecutionJobKinds.Legacy || job.CompletedAt.HasValue)
            return LegacyExpirationAction.None;

        if (job.Status == "running")
        {
            if ((job.StartedAt ?? job.CreatedAt) > now - runningTimeout)
                return LegacyExpirationAction.None;
            return job.AttemptCount >= maxAttempts
                ? LegacyExpirationAction.Fail
                : LegacyExpirationAction.Requeue;
        }

        if (job.Status == "queued" &&
            (job.AttemptCount >= maxAttempts || job.CreatedAt <= now - queuedTimeout))
            return LegacyExpirationAction.Fail;

        return LegacyExpirationAction.None;
    }
}
