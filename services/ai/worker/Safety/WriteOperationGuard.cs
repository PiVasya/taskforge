using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Options;

namespace TaskForge.AiAgent.Safety;

public sealed class WriteOperationGuard
{
    private readonly TaskForgeAgentOptions _options;
    public WriteOperationGuard(IOptions<TaskForgeAgentOptions> options) => _options = options.Value;

    public ApprovalRequestSpec RequireApproval(string operation, string reason, System.Text.Json.Nodes.JsonObject payload)
    {
        return new ApprovalRequestSpec
        {
            Operation = operation,
            Reason = reason,
            Payload = payload
        };
    }

    public void ThrowIfDangerousWritesAreDisabled(string operation)
    {
        if (!_options.EnableDangerousWriteTools)
            throw new InvalidOperationException($"Direct write tool '{operation}' is disabled. Use approval artifact instead.");
    }
}
