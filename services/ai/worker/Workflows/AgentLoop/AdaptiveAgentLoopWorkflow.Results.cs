using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed partial class AdaptiveAgentLoopWorkflow
{
    private async Task<JsonObject> AnswerDirectlyAsync(AgentLoopState state, CancellationToken cancellationToken)
    {
        var stateJson = state.ToJsonObject(_options.MaxAgentStateCharacters).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var prompt = $$"""
Ты AI-ассистент TaskForge. Ответь пользователю по-русски, как нормальный продуктовый помощник.
Учитывай весь AgentState: историю чата, контекст курса/задания, наблюдения и ограничения.
Не делай вид, что ты выполнил запись в систему, если записи не было. Если нужны действия в курсе — предложи безопасный следующий шаг.
Не показывай служебный/raw JSON без просьбы. Ответ должен быть полезным и конкретным.

AgentState:
{{stateJson}}
""";
        var answer = await _llm.CompleteAsync(prompt, cancellationToken);
        state.FinalMessage = string.IsNullOrWhiteSpace(answer) ? "Готово." : answer.Trim();
        state.ScenarioId = "open_chat";
        state.Finished = true;
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = "Подготовлен обычный ответ в чат с учётом общей памяти run-а.",
            ["answerPreview"] = Trim(state.FinalMessage, 1000)
        };
    }

    private AgentResultEnvelope BuildResult(AgentLoopState state)
    {
        if (state.DelegatedResult is not null)
        {
            state.DelegatedResult.MemoryPatch["agentLoop"] = new JsonObject
            {
                ["enabled"] = true,
                ["steps"] = state.Trace.Count,
                ["selectedWorkflow"] = state.SelectedWorkflow,
                ["scenarioId"] = state.ScenarioId,
                ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };
            return state.DelegatedResult;
        }

        var workflowState = new WorkflowState
        {
            Job = state.Job,
            WorkflowName = Name,
            ScenarioId = state.ScenarioId
        };
        workflowState.AssistantMessage = string.IsNullOrWhiteSpace(state.FinalMessage)
            ? "Я не смог получить полноценный результат от AI-цикла. Попробуй уточнить запрос или приложить контекст курса."
            : state.FinalMessage;
        workflowState.MemoryPatch["agentLoop"] = new JsonObject
        {
            ["enabled"] = true,
            ["steps"] = state.Trace.Count,
            ["scenarioId"] = state.ScenarioId,
            ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        workflowState.Data["agentLoop"] = state.ToJsonObject(_options.MaxAgentStateCharacters);
        if (state.WorkingMemory["pendingPatchSet"] is JsonObject patchSet)
        {
            workflowState.Artifacts.Add(new AgentArtifact(
                patchSet["type"]?.ToString() ?? "course_patch_set",
                patchSet["title"]?.ToString() ?? "Патч курса",
                patchSet.DeepClone()));
            workflowState.AssistantMessage = string.IsNullOrWhiteSpace(workflowState.AssistantMessage)
                ? patchSet["assistantMessage"]?.ToString() ?? "Подготовил patch set. Открой меню патчей, проверь диффы и только потом применяй."
                : workflowState.AssistantMessage;
            workflowState.ScenarioId = "course_patch_set";
        }
        foreach (var note in state.Notes)
            workflowState.Notes.Add(note);

        return _envelopes.FromWorkflowState(workflowState);
    }

    private static string NormalizeAction(string? action)
        => string.IsNullOrWhiteSpace(action) ? "inspect_context" : action.Trim().ToLowerInvariant();

    private static string BuildObservationSummary(JsonObject observation)
        => observation["summary"]?.ToString()
           ?? observation["message"]?.ToString()
           ?? (observation["ok"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true ? "Шаг выполнен." : "Шаг завершён с замечанием.");

    private static string GetActionTitle(string action) => action switch
    {
        "inspect_context" => "изучить контекст",
        "classify_request" => "понять запрос",
        "load_editable_assignments" => "открыть задания курса",
        "map_course_structure" => "построить карту курса",
        "extract_course_style" => "извлечь стиль курса",
        "find_learning_gaps" => "найти пробелы курса",
        "analyze_assignment_complexity" => "оценить сложность заданий",
        "plan_course_enrichment" => "подготовить план улучшения курса",
        "search_course" => "найти задания в курсе",
        "propose_assignment_patch_set" => "подготовить патчи",
        "review_patch_set" => "проверить патчи",
        "review_delegated_result" => "проверить результат",
        "delegate_assignment_draft" => "создать задания",
        "delegate_course_audit" => "проанализировать курс",
        "delegate_course_edit" => "подготовить правки курса",
        "delegate_polish_assignment" => "доработать задание",
        "answer_directly" => "ответить в чат",
        "finish" => "завершить",
        _ => action
    };

    private static string GetWorkflowTitle(string workflowName) => workflowName switch
    {
        "assignment_draft_workflow" => "генератор заданий",
        "course_audit_workflow" => "анализ курса",
        "course_edit_workflow" => "правки курса",
        "polish_assignment_draft" => "доработка задания",
        _ => workflowName
    };

    private static JsonNode? ClonePayloadProperty(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value))
            return null;
        try
        {
            return JsonNode.Parse(value.GetRawText())?.DeepClone();
        }
        catch
        {
            return JsonValue.Create(value.ToString());
        }
    }

    private static IEnumerable<string> GetPayloadKeys(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var property in payload.EnumerateObject())
            yield return property.Name;
    }

    private static bool LooksLikeDraftRequest(string text)
    {
        var t = text.ToLowerInvariant();
        return (t.Contains("создай") || t.Contains("сгенер") || t.Contains("придум") || t.Contains("сделай") || t.Contains("накидай"))
               && (t.Contains("задач") || t.Contains("задани") || t.Contains("курс"));
    }

    private static string Trim(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
