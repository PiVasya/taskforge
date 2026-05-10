using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Workflows.Executors;

namespace TaskForge.AiAgent.Workflows;

public sealed class OpenChatWorkflow : ITaskForgeWorkflow
{
    private readonly LoadRunContextExecutor _loadContext;
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly ResultEnvelopeBuilder _envelopes;
    private readonly AgentStepReporter _steps;
    private readonly TaskForgeAgentOptions _options;
    private AIAgent? _agent;

    public OpenChatWorkflow(
        LoadRunContextExecutor loadContext,
        TaskForgeAgentFactory agentFactory,
        AgentSessionStore sessionStore,
        ResultEnvelopeBuilder envelopes,
        AgentStepReporter steps,
        IOptions<TaskForgeAgentOptions> options)
    {
        _loadContext = loadContext;
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _envelopes = envelopes;
        _steps = steps;
        _options = options.Value;
    }

    public string Name => "open_chat";
    public int Priority => 0;
    public bool CanHandle(ClaimedAgentJob job) => true;

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var state = new WorkflowState { Job = job, WorkflowName = Name, ScenarioId = "open_chat" };
        var context = await _loadContext.ExecuteAsync(state);
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        await _steps.TryReportAsync("agent", "running", "Запускаю TaskForgeCoordinator", "Open-chat workflow с доступом к C# tools.");

        var session = await _sessionStore.LoadAsync(_agent, job.ConversationId, cancellationToken);
        var prompt = $$"""
{{TaskForgeAgentPrompts.Coordinator}}

Ответь пользователю. Используй tools, если нужны точные данные курса/заданий/тестов.
Если пользователь просит создать/изменить/сохранить данные — не выполняй запись напрямую, предложи workflow/approval.

{{context}}

{{TaskForgeAgentPrompts.StructuredEnvelope}}
""";
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await _sessionStore.SaveAsync(_agent, session, job.ConversationId, cancellationToken);
        await _steps.TryReportAsync("agent", "completed", "Ответ агента получен", response.Text);

        var envelope = ResultEnvelopeBuilder.FromRawAgentText(response.Text ?? string.Empty, job, "open_chat");
        envelope.Debug["workflow"] = Name;
        envelope.Debug["model"] = _options.Model;
        return envelope;
    }
}
