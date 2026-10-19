namespace TaskForge.Execution.Api.Services.Legacy;

// Identity metadata only: never record submitted code, tests, input, verdict bodies, or API keys.
internal static class CodeQueueTrace
{
    internal const string WorkerHeader = "X-TaskForge-Execution-Worker";
    internal const string JobHeader = "X-TaskForge-Execution-Job";
    internal const string AttemptHeader = "X-TaskForge-Execution-Attempt";
    internal static readonly string ApiInstance = NewApiInstance();

    private static string NewApiInstance()
    {
        var node = WorkerLabel(Environment.GetEnvironmentVariable("TASKFORGE_NODE_LABEL") ?? "unlabeled");
        if (node.Length > 32) node = "unlabeled";
        var host = Environment.MachineName;
        if (host.Length > 40) host = host[..40];
        return $"{node}/{host}/{Guid.NewGuid():N}";
    }

    internal static string WorkerLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown-pre-upgrade-worker";
        if (value.Length > 120 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':' or '/')))
            return "invalid-worker-label";
        return value;
    }

    internal static string FromRequest(HttpContext http)
        => WorkerLabel(http.Request.Headers[WorkerHeader].ToString());

    internal static long AgeSeconds(DateTimeOffset now, DateTimeOffset timestamp)
        => Math.Max(0, (long)(now - timestamp).TotalSeconds);
}
