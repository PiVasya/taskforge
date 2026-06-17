using System.Text.Json.Nodes;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed class LoadRunContextExecutor
{
    private readonly PromptContextComposer _composer;
    private readonly AgentStepReporter _steps;

    public LoadRunContextExecutor(PromptContextComposer composer, AgentStepReporter steps)
    {
        _composer = composer;
        _steps = steps;
    }

    public async Task<string> ExecuteAsync(WorkflowState state)
    {
        await _steps.TryReportAsync("context", "running", "Собираю контекст TaskForge", "Получаю данные курса, задания и последних сообщений.");
        var prompt = _composer.ComposeRunPrompt(state.Job, state.WorkflowName);
        state.Data["contextPromptLength"] = prompt.Length;
        state.Notes.Add($"Context prompt length: {prompt.Length}");
        await _steps.TryReportAsync("context", "completed", "Контекст загружен", $"Получены данные для ответа: {prompt.Length} символов.", new JsonObject { ["length"] = prompt.Length });
        return prompt;
    }
}
