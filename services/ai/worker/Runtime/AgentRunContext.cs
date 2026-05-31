using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Infrastructure;

namespace TaskForge.AiAgent.Runtime;

public sealed class AgentRunContext
{
    public AgentRunContext(ClaimedAgentJob job, TaskForgeInternalApiClient api, string workerId, CancellationToken cancellationToken)
    {
        Job = job;
        Api = api;
        WorkerId = workerId;
        CancellationToken = cancellationToken;
    }

    public ClaimedAgentJob Job { get; }
    public TaskForgeInternalApiClient Api { get; }
    public string WorkerId { get; }
    public CancellationToken CancellationToken { get; }
}

public sealed class AgentRunContextAccessor
{
    private readonly AsyncLocal<AgentRunContext?> _current = new();
    public AgentRunContext Current => _current.Value ?? throw new InvalidOperationException("No active TaskForge agent run context.");

    public IDisposable Push(AgentRunContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Popper(() => _current.Value = previous);
    }

    private sealed class Popper : IDisposable
    {
        private readonly Action _pop;
        private bool _disposed;
        public Popper(Action pop) => _pop = pop;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pop();
        }
    }
}
