using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Workflows.Executors;

namespace TaskForge.AiAgent.Workflows;

public sealed class CourseEditWorkflow : ITaskForgeWorkflow
{
    private readonly LoadRunContextExecutor _loadContext;
    private readonly PlanRequestExecutor _planner;
    private readonly ApprovalGateExecutor _approval;
    private readonly ResultEnvelopeBuilder _envelopes;
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private AIAgent? _agent;

    public CourseEditWorkflow(
        LoadRunContextExecutor loadContext,
        PlanRequestExecutor planner,
        ApprovalGateExecutor approval,
        ResultEnvelopeBuilder envelopes,
        TaskForgeAgentFactory agentFactory,
        AgentSessionStore sessionStore,
        AgentStepReporter steps)
    {
        _loadContext = loadContext;
        _planner = planner;
        _approval = approval;
        _envelopes = envelopes;
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
    }

    public string Name => "course_edit_workflow";
    public int Priority => 80;

    public bool CanHandle(ClaimedAgentJob job)
    {
        var intent = AgentIntentClassifier.Select(job);
        if (intent.IsCourseEditScenario)
            return true;

        var text = job.UserText.ToLowerInvariant();
        return text.Contains("измени курс")
               || text.Contains("измени задани")
               || text.Contains("редакт")
               || text.Contains("примени правки")
               || text.Contains("обнови задания")
               || text.Contains("переделай")
               || text.Contains("исправь")
               || text.Contains("добавь тест")
               || text.Contains("измени тест")
               || text.Contains("переставь")
               || text.Contains("переименуй")
               || text.Contains("подгони")
               || text.Contains("единый стиль")
               || text.Contains("один стиль")
               || text.Contains("нормализуй")
               || text.Contains("выровняй")
               || text.Contains("удали задание");
    }

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var intent = AgentIntentClassifier.Select(job);
        var state = new WorkflowState { Job = job, WorkflowName = Name, ScenarioId = "course_edit" };
        state.Data["agentIntent"] = intent.ToJsonObject();
        var context = await _loadContext.ExecuteAsync(state);
        var plan = await _planner.ExecuteAsync(state, context, cancellationToken);

        _agent ??= _agentFactory.CreateCoordinatorAgent();
        await _steps.TryReportAsync("course_edit", "running", "Готовлю безопасный пакет правок", "Изменения будут вынесены на подтверждение и не применятся автоматически.");
        var session = await _sessionStore.LoadAsync(_agent, job.ConversationId, cancellationToken);
        var prompt = $$"""
{{TaskForgeAgentPrompts.Coordinator}}

Пользователь просит изменить курс. Подготовь только patch, не применяй его.
Patch должен соответствовать proposal artifact course_edit_proposal: { "courseId": "...", "assignments": [], "order": [] }. Этот patch нельзя применять автоматически без отдельного подтверждения пользователя.
Если меняешь publicTests/hiddenTests/testCases у code-test, внутри этого же assignment patch ОБЯЗАТЕЛЬНО добавь referenceSolution/solution на языке задания. Backend перед применением прогонит это решение через runner; без успешного runner patch будет отклонён.
Не добавляй пустые тесты. Для code-test нужно минимум 2 publicTests и 2 hiddenTests, причём hiddenTests должны содержать случаи, которых нет в publicTests.

План:
{{plan}}

Контекст:
{{context}}

Верни строго JSON patch без markdown.
""";
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await _sessionStore.SaveAsync(_agent, session, job.ConversationId, cancellationToken);
        var patch = ParsePatch(response.Text ?? string.Empty, job);
        await _approval.ExecuteForCourseEditAsync(state, patch);
        state.AssistantMessage = "Я подготовил пакет правок курса, но не применял их автоматически. Проверь предложение и подтверди изменения.";
        return _envelopes.FromWorkflowState(state);
    }

    private static JsonObject ParsePatch(string text, ClaimedAgentJob job)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start && JsonNode.Parse(text[start..(end + 1)]) is JsonObject obj)
            {
                obj["courseId"] ??= job.CourseId?.ToString();
                return obj;
            }
        }
        catch
        {
        }

        return new JsonObject
        {
            ["courseId"] = job.CourseId?.ToString(),
            ["assignments"] = new JsonArray(),
            ["order"] = new JsonArray(),
            ["parseFallback"] = true,
            ["raw"] = text.Length > 4000 ? text[..4000] : text
        };
    }
}
