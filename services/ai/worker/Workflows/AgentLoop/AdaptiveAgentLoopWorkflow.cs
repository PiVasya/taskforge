using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed partial class AdaptiveAgentLoopWorkflow : ITaskForgeWorkflow
{
    private readonly AgentLoopDecisionClient _decisionClient;
    private readonly PromptContextComposer _contextComposer;
    private readonly DirectLlmTextClient _llm;
    private readonly AgentStepReporter _steps;
    private readonly ResultEnvelopeBuilder _envelopes;
    private readonly AssignmentDraftWorkflow _assignmentDraft;
    private readonly CourseAuditWorkflow _courseAudit;
    private readonly CourseEditWorkflow _courseEdit;
    private readonly PolishAssignmentDraftWorkflow _polish;
    private readonly TaskForgeAgentOptions _options;

    public AdaptiveAgentLoopWorkflow(
        AgentLoopDecisionClient decisionClient,
        PromptContextComposer contextComposer,
        DirectLlmTextClient llm,
        AgentStepReporter steps,
        ResultEnvelopeBuilder envelopes,
        AssignmentDraftWorkflow assignmentDraft,
        CourseAuditWorkflow courseAudit,
        CourseEditWorkflow courseEdit,
        PolishAssignmentDraftWorkflow polish,
        IOptions<TaskForgeAgentOptions> options)
    {
        _decisionClient = decisionClient;
        _contextComposer = contextComposer;
        _llm = llm;
        _steps = steps;
        _envelopes = envelopes;
        _assignmentDraft = assignmentDraft;
        _courseAudit = courseAudit;
        _courseEdit = courseEdit;
        _polish = polish;
        _options = options.Value;
    }

    public string Name => "adaptive_agent_loop";
    public int Priority => 1000;
    public bool CanHandle(ClaimedAgentJob job) => _options.EnableAdaptiveAgentLoop;

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var state = new AgentLoopState { Job = job };
        await _steps.TryReportAsync(
            "agent_loop",
            "running",
            "Запущен управляемый AI-цикл",
            "Модель будет выбирать следующий шаг из безопасного набора действий, а система сохранит память и логи каждого шага.",
            new JsonObject
            {
                ["runId"] = job.RunId.ToString(),
                ["conversationId"] = job.ConversationId.ToString(),
                ["jobType"] = job.JobType,
                ["model"] = _options.Model,
                ["maxSteps"] = _options.MaxAgentLoopSteps
            });

        for (var step = 1; step <= _options.MaxAgentLoopSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.StepNumber = step;
            var decision = await _decisionClient.ChooseNextActionAsync(state, cancellationToken);
            var calls = decision.GetActionCalls().Take(_options.MaxAgentActionsPerStep).ToList();
            if (calls.Count == 0)
                calls.Add(new AgentLoopActionCall { Action = "inspect_context", ReasonSummary = "Система подставила безопасный шаг, потому что модель вернула пустой пакет." });

            await _steps.TryReportAsync(
                "agent_decision",
                "running",
                calls.Count == 1
                    ? $"AI выбрал шаг: {GetActionTitle(NormalizeAction(calls[0].Action))}"
                    : $"AI выбрал пакет действий: {calls.Count}",
                Trim(decision.ReasonSummary, 1000),
                new JsonObject
                {
                    ["step"] = step,
                    ["batchSize"] = calls.Count,
                    ["decision"] = new JsonObject
                    {
                        ["reasonSummary"] = Trim(decision.ReasonSummary, 1000),
                        ["actions"] = new JsonArray(calls.Select((x, index) => new JsonObject
                        {
                            ["index"] = index + 1,
                            ["action"] = NormalizeAction(x.Action),
                            ["reasonSummary"] = Trim(x.ReasonSummary, 1000),
                            ["args"] = x.Args.DeepClone()
                        }).ToArray<JsonNode?>())
                    },
                    ["stateBefore"] = state.ToJsonObject(_options.MaxAgentStateCharacters)
                });

            for (var batchIndex = 0; batchIndex < calls.Count; batchIndex++)
            {
                var call = calls[batchIndex];
                var trace = new AgentLoopTraceEntry
                {
                    Step = step,
                    BatchIndex = batchIndex + 1,
                    Action = NormalizeAction(call.Action),
                    ReasonSummary = Trim(call.ReasonSummary, 1000),
                    Args = call.Args.DeepClone().AsObject()
                };

                try
                {
                    trace.Observation = await ExecuteActionAsync(state, trace.Action, trace.Args, cancellationToken);
                    trace.Status = "completed";
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    trace.Status = "failed";
                    trace.Observation = new JsonObject
                    {
                        ["ok"] = false,
                        ["errorType"] = ex.GetType().Name,
                        ["message"] = ex.Message
                    };
                    state.Notes.Add($"Action {trace.Action} failed: {ex.GetType().Name}: {ex.Message}");
                }

                state.Trace.Add(trace);
                await _steps.TryReportAsync(
                    "agent_action",
                    trace.Status,
                    calls.Count == 1
                        ? $"Шаг завершён: {GetActionTitle(trace.Action)}"
                        : $"Действие {batchIndex + 1}/{calls.Count}: {GetActionTitle(trace.Action)}",
                    BuildObservationSummary(trace.Observation),
                    new JsonObject
                    {
                        ["step"] = step,
                        ["batchIndex"] = batchIndex + 1,
                        ["batchSize"] = calls.Count,
                        ["action"] = trace.Action,
                        ["reasonSummary"] = trace.ReasonSummary,
                        ["args"] = trace.Args.DeepClone(),
                        ["observation"] = trace.Observation.DeepClone(),
                        ["stateAfter"] = state.ToJsonObject(_options.MaxAgentStateCharacters)
                    });

                if (state.Finished)
                    break;
            }

            if (state.Finished)
                break;
        }

        if (!state.Finished)
        {
            state.Notes.Add("Agent loop stopped by max step limit.");
            await _steps.TryReportAsync(
                "agent_loop",
                "completed_with_warnings",
                "AI-цикл остановлен по лимиту шагов",
                "Система не дала агенту уйти в бесконечные улучшения. Возвращаю лучшее доступное состояние.",
                state.ToJsonObject(_options.MaxAgentStateCharacters));
        }

        var result = BuildResult(state);
        result.Debug["agentLoop"] = state.ToJsonObject(_options.MaxAgentStateCharacters);
        return result;
    }

}
