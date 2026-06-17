using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed class AdaptiveAgentLoopWorkflow : ITaskForgeWorkflow
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

    private async Task<JsonObject> ExecuteActionAsync(AgentLoopState state, string action, JsonObject args, CancellationToken cancellationToken)
    {
        if (state.DelegatedResult is not null && action.StartsWith("delegate_", StringComparison.OrdinalIgnoreCase))
        {
            state.Notes.Add($"Delegation '{action}' was ignored: a workflow result already exists.");
            return new JsonObject
            {
                ["ok"] = false,
                ["message"] = "Результат рабочего сценария уже получен. Следующий шаг должен проверить итог и завершить run."
            };
        }

        if (action.StartsWith("delegate_", StringComparison.OrdinalIgnoreCase))
        {
            if (!state.LoadedContext)
            {
                state.Notes.Add($"Delegation '{action}' was delayed: context has not been inspected yet.");
                return new JsonObject
                {
                    ["ok"] = false,
                    ["message"] = "Сначала нужно выполнить inspect_context, чтобы не потерять контекст чата и курса."
                };
            }

            if (!state.WorkingMemory.ContainsKey("intent"))
            {
                state.Notes.Add($"Delegation '{action}' was delayed: request has not been classified yet.");
                return new JsonObject
                {
                    ["ok"] = false,
                    ["message"] = "Сначала нужно выполнить classify_request, чтобы выбрать сценарий осознанно."
                };
            }
        }

        switch (action)
        {
            case "inspect_context":
                return InspectContext(state);
            case "classify_request":
                return ClassifyRequest(state);
            case "load_editable_assignments":
                return CourseAgentTools.LoadEditableAssignments(state);
            case "map_course_structure":
                return CourseAgentTools.MapCourseStructure(state);
            case "extract_course_style":
                return CourseAgentTools.ExtractCourseStyle(state);
            case "find_learning_gaps":
                return CourseAgentTools.FindLearningGaps(state);
            case "analyze_assignment_complexity":
                return CourseAgentTools.AnalyzeAssignmentComplexity(state);
            case "plan_course_enrichment":
                return CourseAgentTools.PlanCourseEnrichment(state);
            case "search_course":
                return CourseAgentTools.SearchCourse(state, args);
            case "propose_assignment_patch_set":
                return CourseAgentTools.ProposeAssignmentPatchSet(state, args, _options.MaxPatchOperationsPerRun);
            case "review_patch_set":
                return CourseAgentTools.ReviewPatchSet(state);
            case "review_delegated_result":
                return CourseAgentTools.ReviewDelegatedResult(state);
            case "delegate_assignment_draft":
                return await DelegateWorkflowAsync(state, _assignmentDraft, "assignment_draft_workflow", cancellationToken);
            case "delegate_course_audit":
                return await DelegateWorkflowAsync(state, _courseAudit, "course_audit_workflow", cancellationToken);
            case "delegate_course_edit":
                return await DelegateWorkflowAsync(state, _courseEdit, "course_edit_workflow", cancellationToken);
            case "delegate_polish_assignment":
                return await DelegateWorkflowAsync(state, _polish, "polish_assignment_draft", cancellationToken);
            case "answer_directly":
                return await AnswerDirectlyAsync(state, cancellationToken);
            case "finish":
                var hasPatchSet = state.WorkingMemory.ContainsKey("pendingPatchSet");
                if (state.DelegatedResult is null && !hasPatchSet && string.IsNullOrWhiteSpace(state.FinalMessage))
                {
                    state.Notes.Add("Model tried to finish before producing a result; finish was ignored.");
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["finished"] = false,
                        ["message"] = "Нельзя завершить run без результата. Нужно выбрать рабочий шаг."
                    };
                }
                if (hasPatchSet && !state.WorkingMemory.ContainsKey("patchSetReview"))
                {
                    state.Notes.Add("Finish was delayed: pending patch set must be reviewed first.");
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["finished"] = false,
                        ["message"] = "Сначала выполни review_patch_set, чтобы не завершать run без проверки патчей."
                    };
                }
                if (state.DelegatedResult is not null && !state.WorkingMemory.ContainsKey("delegatedResultReview"))
                {
                    state.Notes.Add("Finish was delayed: delegated workflow result must be reviewed first.");
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["finished"] = false,
                        ["message"] = "Сначала выполни review_delegated_result, чтобы не завершать run без проверки результата."
                    };
                }
                state.Finished = true;
                return new JsonObject
                {
                    ["ok"] = true,
                    ["finished"] = true,
                    ["hasDelegatedResult"] = state.DelegatedResult is not null,
                    ["hasPatchSet"] = state.WorkingMemory.ContainsKey("pendingPatchSet"),
                    ["hasFinalMessage"] = !string.IsNullOrWhiteSpace(state.FinalMessage)
                };
            default:
                return new JsonObject
                {
                    ["ok"] = false,
                    ["message"] = $"Unknown action: {action}"
                };
        }
    }

    private JsonObject InspectContext(AgentLoopState state)
    {
        var payload = state.Job.Payload;
        var memory = state.WorkingMemory;
        memory["contextLoadedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        memory["contextPrompt"] = Trim(_contextComposer.ComposeRunPrompt(state.Job, Name), _options.MaxContextCharacters);
        memory["payloadKeys"] = new JsonArray(GetPayloadKeys(payload).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        memory["courseDigest"] = ClonePayloadProperty(payload, "courseDigest") ?? new JsonObject
        {
            ["selectedCourseId"] = state.Job.CourseId?.ToString(),
            ["selectedAssignmentId"] = state.Job.AssignmentId?.ToString()
        };
        memory["assignments"] = ClonePayloadProperty(payload, "assignments") ?? new JsonArray();
        memory["courseOutline"] = ClonePayloadProperty(payload, "courseOutline") ?? new JsonArray();
        memory["targetAssignments"] = ClonePayloadProperty(payload, "targetAssignments") ?? new JsonArray();
        memory["focusAssignments"] = ClonePayloadProperty(payload, "focusAssignments") ?? new JsonArray();
        memory["recentMessages"] = ClonePayloadProperty(payload, "recentMessages") ?? new JsonArray();
        memory["conversationState"] = ClonePayloadProperty(payload, "conversationState") ?? new JsonObject();
        memory["contextWarnings"] = ClonePayloadProperty(payload, "contextWarnings") ?? new JsonArray();
        memory["rawUserRequest"] = state.Job.UserText;
        state.LoadedContext = true;
        state.Notes.Add("Context inspected and copied into shared working memory.");

        return new JsonObject
        {
            ["ok"] = true,
            ["loadedContext"] = true,
            ["payloadKeys"] = memory["payloadKeys"]?.DeepClone(),
            ["courseDigest"] = memory["courseDigest"]?.DeepClone(),
            ["conversationStateAvailable"] = memory["conversationState"] is JsonObject { Count: > 0 },
            ["summary"] = "Контекст запроса, чата, курса и заданий сохранён в общей памяти run-а."
        };
    }

    private JsonObject ClassifyRequest(AgentLoopState state)
    {
        var intent = AgentIntentClassifier.Select(state.Job);
        var text = state.Job.UserText.ToLowerInvariant();
        var needsCourse = text.Contains("курс") || text.Contains("в стиле") || text.Contains("как в курсе") || text.Contains("пробел") || text.Contains("скач");
        var needsDrafts = intent.IsDraftScenario || LooksLikeDraftRequest(text);
        var intentJson = intent.ToJsonObject();
        intentJson["needsCourseContext"] = needsCourse;
        intentJson["needsAssignmentDrafts"] = needsDrafts;
        intentJson["detectedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        state.WorkingMemory["intent"] = intentJson.DeepClone();
        state.ScenarioId = intent.ScenarioId == "free_chat" && needsDrafts ? "style_matched_tasks" : intent.ScenarioId;
        state.Notes.Add($"Request classified as {state.ScenarioId}.");

        return new JsonObject
        {
            ["ok"] = true,
            ["intent"] = intentJson,
            ["summary"] = $"Определён сценарий: {state.ScenarioId}."
        };
    }

    private async Task<JsonObject> DelegateWorkflowAsync(AgentLoopState state, ITaskForgeWorkflow workflow, string workflowName, CancellationToken cancellationToken)
    {
        state.SelectedWorkflow = workflowName;
        state.Notes.Add($"Delegating to {workflowName}.");
        await _steps.TryReportAsync(
            "agent_delegate",
            "running",
            $"Передаю работу сценарию: {GetWorkflowTitle(workflowName)}",
            "Передача идёт не вслепую: вся рабочая память и уже собранный контекст сохранены в логе run-а.",
            new JsonObject
            {
                ["workflow"] = workflowName,
                ["state"] = state.ToJsonObject(_options.MaxAgentStateCharacters)
            });

        var delegatedJob = BuildDelegatedJob(state, workflowName);
        var result = await workflow.RunAsync(delegatedJob, cancellationToken);
        state.DelegatedResult = result;
        state.FinalMessage = result.AssistantMessage;
        state.ScenarioId = string.IsNullOrWhiteSpace(result.ScenarioId) ? state.ScenarioId : result.ScenarioId;
        state.Notes.Add($"Workflow {workflowName} returned a result. Model will receive the observation and decide whether to finish.");

        return new JsonObject
        {
            ["ok"] = true,
            ["workflow"] = workflowName,
            ["scenarioId"] = result.ScenarioId,
            ["assistantMessagePreview"] = Trim(result.AssistantMessage, 1000),
            ["artifactCount"] = result.Artifacts.Count,
            ["summary"] = $"Сценарий {GetWorkflowTitle(workflowName)} завершён, результат сохранён в памяти agent loop."
        };
    }


    private ClaimedAgentJob BuildDelegatedJob(AgentLoopState state, string workflowName)
    {
        var payload = ToMutablePayload(state.Job.Payload);
        var traceSnapshot = new JsonArray(state.Trace.Select(x => x.ToJsonObject()).ToArray<JsonNode?>());
        var noteSnapshot = new JsonArray(state.Notes.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        var agentLoopMemory = new JsonObject
        {
            ["enabled"] = true,
            ["sourceWorkflow"] = Name,
            ["targetWorkflow"] = workflowName,
            ["stepNumber"] = state.StepNumber,
            ["scenarioId"] = state.ScenarioId,
            ["selectedWorkflow"] = state.SelectedWorkflow,
            ["workingMemory"] = state.WorkingMemory.DeepClone(),
            ["trace"] = traceSnapshot.DeepClone(),
            ["notes"] = noteSnapshot.DeepClone(),
            ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };

        payload["agentLoopMemory"] = agentLoopMemory.DeepClone();
        CopyWorkingMemoryNode(payload, state, "courseMap");
        CopyWorkingMemoryNode(payload, state, "courseStyleProfile");
        CopyWorkingMemoryNode(payload, state, "courseGapReport");
        CopyWorkingMemoryNode(payload, state, "assignmentComplexityReport");
        CopyWorkingMemoryNode(payload, state, "pendingPatchSet");
        CopyWorkingMemoryNode(payload, state, "courseEnrichmentBrief");
        CopyWorkingMemoryNode(payload, state, "delegatedResultReview");

        var memory = payload["memory"] as JsonObject ?? new JsonObject();
        memory["agentLoop"] = new JsonObject
        {
            ["scenarioId"] = state.ScenarioId,
            ["selectedWorkflow"] = workflowName,
            ["workingMemory"] = state.WorkingMemory.DeepClone(),
            ["lastTrace"] = traceSnapshot.DeepClone(),
            ["notes"] = noteSnapshot.DeepClone()
        };
        payload["memory"] = memory;

        var enrichedPayload = ToJsonElement(payload);
        return state.Job with { Payload = enrichedPayload };
    }

    private static void CopyWorkingMemoryNode(JsonObject payload, AgentLoopState state, string key)
    {
        if (state.WorkingMemory.TryGetPropertyValue(key, out var node) && node is not null)
            payload[key] = node.DeepClone();
    }

    private static JsonObject ToMutablePayload(JsonElement payload)
    {
        try
        {
            if (payload.ValueKind == JsonValueKind.Object && JsonNode.Parse(payload.GetRawText()) is JsonObject obj)
                return obj.DeepClone().AsObject();
        }
        catch
        {
            // Fall through to wrapper below.
        }

        return new JsonObject
        {
            ["rawPayload"] = payload.ValueKind == JsonValueKind.Undefined ? null : payload.GetRawText()
        };
    }

    private static JsonElement ToJsonElement(JsonObject payload)
    {
        using var document = JsonDocument.Parse(payload.ToJsonString());
        return document.RootElement.Clone();
    }

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
