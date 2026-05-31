using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed class CourseAuditExecutor
{
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private AIAgent? _agent;

    public CourseAuditExecutor(TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps)
    {
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
    }

    public async Task<JsonObject> ExecuteAsync(WorkflowState state, string contextPrompt, CancellationToken cancellationToken)
    {
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        await _steps.TryReportAsync("audit", "running", "Анализирую курс и пробелы", state.Job.CourseTitle);
        var session = await _sessionStore.LoadAsync(_agent, state.Job.ConversationId, cancellationToken);
        var prompt = $$"""
Сделай глубокий аудит курса TaskForge.
Найди пробелы, резкие скачки сложности, слабые места в тестах, отсутствующие bridge tasks и предложения по улучшению.
Не применяй изменения автоматически.
Верни строго JSON:
{
  "summary": "...",
  "findings": [{"severity":"low|medium|high","title":"...","evidence":"...","recommendation":"..."}],
  "suggestedTasks": [{"title":"...","reason":"...","difficulty":1}],
  "courseId": "..."
}

{{contextPrompt}}
""";
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);
        var audit = ParseAudit(response.Text ?? string.Empty, state);
        await _steps.TryReportAsync("audit", "completed", "Аудит курса готов", audit["summary"]?.ToString(), audit);
        return audit;
    }

    private static JsonObject ParseAudit(string text, WorkflowState state)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start && JsonNode.Parse(text[start..(end + 1)]) is JsonObject obj)
                return obj;
        }
        catch
        {
        }

        return new JsonObject
        {
            ["summary"] = text.Length > 1200 ? text[..1200] : text,
            ["findings"] = new JsonArray(),
            ["suggestedTasks"] = new JsonArray(),
            ["courseId"] = state.Job.CourseId?.ToString(),
            ["parseFallback"] = true
        };
    }
}
