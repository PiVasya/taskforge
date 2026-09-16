using System.Net;

namespace TaskForge.Solutions.RatingWorker;

internal static class TasksApiRetryPolicy
{
    internal const int DefaultMaxAttempts = 8;

    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        ILogger logger,
        string operation,
        CancellationToken cancellationToken,
        int maxAttempts = DefaultMaxAttempts,
        Func<int, TimeSpan>? delayFactory = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(logger);

        maxAttempts = Math.Clamp(maxAttempts, 1, 12);
        delayFactory ??= DefaultDelay;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!ShouldRetry(response.StatusCode) || attempt == maxAttempts)
                {
                    if (response.IsSuccessStatusCode && attempt > 1)
                    {
                        logger.LogInformation(
                            "Tasks API recovered for {Operation} on attempt {Attempt}/{MaxAttempts}.",
                            operation,
                            attempt,
                            maxAttempts);
                    }
                    return response;
                }

                var status = (int)response.StatusCode;
                response.Dispose();
                var delay = delayFactory(attempt);
                logger.LogWarning(
                    "Tasks API not ready for {Operation}: HTTP {Status}. Retry {NextAttempt}/{MaxAttempts} in {DelayMs} ms.",
                    operation,
                    status,
                    attempt + 1,
                    maxAttempts,
                    (long)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                var delay = delayFactory(attempt);
                logger.LogWarning(
                    ex,
                    "Tasks API connection failed for {Operation}. Retry {NextAttempt}/{MaxAttempts} in {DelayMs} ms.",
                    operation,
                    attempt + 1,
                    maxAttempts,
                    (long)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Tasks API retry loop completed without a response.");
    }

    internal static bool ShouldRetry(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;

    private static TimeSpan DefaultDelay(int failedAttempt)
    {
        var milliseconds = failedAttempt switch
        {
            <= 1 => 500,
            2 => 1000,
            3 => 2000,
            4 => 4000,
            5 => 8000,
            _ => 10000
        };
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
