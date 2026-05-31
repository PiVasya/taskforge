using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TaskForge.AiAgent.Observability;

public sealed class AgentTelemetry
{
    private readonly ILogger<AgentTelemetry> _logger;
    public AgentTelemetry(ILogger<AgentTelemetry> logger) => _logger = logger;

    public IDisposable Measure(string operation, Guid runId)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("AI operation started: {Operation} run={RunId}", operation, runId);
        return new DisposableAction(() =>
        {
            sw.Stop();
            _logger.LogInformation("AI operation finished: {Operation} run={RunId} elapsedMs={ElapsedMs}", operation, runId, sw.ElapsedMilliseconds);
        });
    }

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _action;
        private bool _disposed;
        public DisposableAction(Action action) => _action = action;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _action();
        }
    }
}
