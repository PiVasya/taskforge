using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Runtime;

public sealed class AgentStepReporter
{
    private readonly AgentRunContextAccessor _contextAccessor;
    private readonly ILogger<AgentStepReporter> _logger;

    public AgentStepReporter(AgentRunContextAccessor contextAccessor, ILogger<AgentStepReporter> logger)
    {
        _contextAccessor = contextAccessor;
        _logger = logger;
    }

    public async Task ReportAsync(string kind, string status, string title, string? summary = null, JsonNode? data = null)
    {
        var context = _contextAccessor.Current;
        await context.Api.AppendStepAsync(context.Job.RunId, context.WorkerId, new AgentStepPayload
        {
            Kind = kind,
            Status = status,
            ActionName = kind,
            Title = title,
            Summary = summary,
            Data = data
        }, context.CancellationToken);
    }

    public async Task TryReportAsync(string kind, string status, string title, string? summary = null, JsonNode? data = null)
    {
        try
        {
            await ReportAsync(kind, status, title, summary, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report TaskForge AI step {Kind}/{Status}", kind, status);
        }
    }
}
