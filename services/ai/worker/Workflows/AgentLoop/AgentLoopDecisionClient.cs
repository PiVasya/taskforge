using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed class AgentLoopDecisionClient
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "inspect_context",
        "classify_request",
        "load_editable_assignments",
        "map_course_structure",
        "extract_course_style",
        "find_learning_gaps",
        "analyze_assignment_complexity",
        "plan_course_enrichment",
        "search_course",
        "propose_assignment_patch_set",
        "review_patch_set",
        "review_delegated_result",
        "delegate_assignment_draft",
        "delegate_course_audit",
        "delegate_course_edit",
        "delegate_polish_assignment",
        "answer_directly",
        "finish"
    };

    private readonly DirectLlmTextClient _llm;
    private readonly TaskForgeAgentOptions _options;
    private readonly ILogger<AgentLoopDecisionClient> _logger;

    public AgentLoopDecisionClient(DirectLlmTextClient llm, IOptions<TaskForgeAgentOptions> options, ILogger<AgentLoopDecisionClient> logger)
    {
        _llm = llm;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentLoopDecision> ChooseNextActionAsync(AgentLoopState state, CancellationToken cancellationToken)
    {
        var prompt = BuildDecisionPrompt(state);
        try
        {
            var raw = await _llm.CompleteAsync(prompt, cancellationToken);
            var decision = ParseDecision(raw);
            var calls = decision.GetActionCalls().Take(_options.MaxAgentActionsPerStep).ToList();
            var unavailable = calls.FirstOrDefault(x => !AllowedActions.Contains(x.Action));
            if (unavailable is not null)
            {
                _logger.LogWarning("Model selected unavailable agent action: {Action}", unavailable.Action);
                return DeterministicFallback(state, $"Модель выбрала недоступное действие '{unavailable.Action}'.");
            }

            if (calls.Count == 0)
                return DeterministicFallback(state, "Модель вернула пустой пакет действий.");

            if (decision.Actions.Count > _options.MaxAgentActionsPerStep)
                state.Notes.Add($"Model returned {decision.Actions.Count} actions; truncated to {_options.MaxAgentActionsPerStep} by MaxAgentActionsPerStep.");

            return decision;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Agent loop decision failed; using deterministic fallback.");
            return DeterministicFallback(state, $"Модель не смогла выбрать следующий шаг: {ex.GetType().Name}.");
        }
    }

    private string BuildDecisionPrompt(AgentLoopState state)
    {
        var compactPayload = Compact(state.Job.Payload.GetRawText(), _options.MaxContextCharacters);
        var stateJson = state.ToJsonObject(_options.MaxAgentStateCharacters).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return $$"""
Ты управляющий AI-агент TaskForge. Твоя задача — выбрать НЕ один шаг, а разумный ПАКЕТ следующих действий из разрешенного списка.
Ты НЕ пишешь финальный ответ, кроме действий answer_directly или finish. Ты не раскрываешь скрытые рассуждения.
Ты видишь общий AgentState: запрос, контекст, историю действий, рабочую память и наблюдения прошлых шагов. Ничего не теряй между шагами: опирайся на state.

Разрешенные действия:
- inspect_context: разобрать payload, историю чата, курс, задания, вложения и записать полезную память в AgentState.
- classify_request: определить намерение, сценарий, ограничения, нужен ли курс/задание/JSON/генерация/анализ/массовая правка.
- load_editable_assignments: открыть и нормализовать все доступные задания курса для дальнейшего анализа и patch set.
- map_course_structure: построить карту курса: порядок, типы, языки, сложность, concept timeline.
- extract_course_style: извлечь стиль существующих заданий: названия, описания, тесты, теги, язык, степень обучающего текста.
- find_learning_gaps: найти пробелы, резкие скачки сложности, слабые тесты, задания без hidden/referenceSolution и места для bridge tasks.
- analyze_assignment_complexity: оценить сложность каждого задания и сохранить reasons/confidence; обязательно перед массовой переразметкой рейтингов/сложности.
- plan_course_enrichment: собрать единый brief для генерации/правок курса из запроса, карты курса, стиля и найденных проблем.
- search_course: найти релевантные задания в уже загруженном курсе. В args передавай {"query":"..."}.
- propose_assignment_patch_set: подготовить безопасный patch set для массового редактирования заданий. В args передавай {"operation":"rerate_assignments"|"retag_assignments"|"update_titles"|"custom","field":"rating"}.
- review_patch_set: проверить patch set перед завершением run.
- review_delegated_result: проверить результат delegate_* перед завершением run.
- delegate_assignment_draft: создать/доработать задания через проверяемый генератор черновиков.
- delegate_course_audit: анализ курса, пробелы, скачки сложности, карта проблем.
- delegate_course_edit: предложение правок курса без прямой записи.
- delegate_polish_assignment: доработка выбранного задания/пакета заданий.
- answer_directly: дать обычный ответ в чат, если не нужен генератор/валидатор/запись.
- finish: завершить, если уже есть FinalMessage, проверенный DelegatedResult или проверенный pendingPatchSet.

Правила выбора пакета:
1. Возвращай actions[] из 1-{{_options.MaxAgentActionsPerStep}} действий. Backend выполнит их по порядку и сохранит логи каждого действия.
2. Если контекст еще не разобран, хороший первый пакет: inspect_context -> classify_request.
3. Если запрос про курс, стиль курса, массовые правки или рейтинги, добавляй load_editable_assignments, map_course_structure, extract_course_style.
4. Если пользователь просит поменять рейтинги/сложность всем заданиям, пакет должен включать: load_editable_assignments -> map_course_structure -> analyze_assignment_complexity -> propose_assignment_patch_set -> review_patch_set -> finish. Если контекст/intent ещё не готовы, добавь inspect_context/classify_request в начало этого же пакета.
5. Для любой работы по курсу не прыгай сразу в delegate_*: сначала при необходимости используй map_course_structure, extract_course_style, find_learning_gaps и plan_course_enrichment.
6. Для запросов на создание/генерацию задач выбирай delegate_assignment_draft только после inspect_context, classify_request и, если курс доступен, extract_course_style/plan_course_enrichment.
7. Для запросов "как в курсе", "в стиле курса", "добавь недостающие", "мостик", "улучшить курс" обязательно используй map_course_structure и extract_course_style до генерации.
8. После delegate_* не завершай сразу: сначала review_delegated_result. После propose_assignment_patch_set не завершай сразу: сначала review_patch_set.
9. Не запускай delegate_* повторно, если delegatedResult уже есть. Повторная делегация будет отклонена системой.
10. Не предлагай автоматическое применение правок без патчей и диффов. Массовые изменения должны идти через patch set.

Верни ТОЛЬКО JSON без markdown. Предпочитай формат actions:
{
  "reasonSummary": "коротко, почему выбран такой пакет",
  "actions": [
    { "action": "inspect_context", "reasonSummary": "сохранить контекст", "args": {} },
    { "action": "classify_request", "reasonSummary": "понять намерение", "args": {} }
  ]
}

Legacy-формат с одним action тоже допустим, но хуже:
{
  "action": "inspect_context",
  "reasonSummary": "коротко, что надо сделать следующим шагом",
  "args": {}
}

AgentState:
{{stateJson}}

RawPayload, доступный на этот run:
{{compactPayload}}
""";
    }

    private AgentLoopDecision DeterministicFallback(AgentLoopState state, string reason)
    {
        if (state.DelegatedResult is not null || !string.IsNullOrWhiteSpace(state.FinalMessage))
        {
            if (state.DelegatedResult is not null && !state.WorkingMemory.ContainsKey("delegatedResultReview"))
                return AgentLoopDecision.Fallback("review_delegated_result", reason + " Результат workflow уже есть; перед завершением проверяю его качество.");
            return AgentLoopDecision.Fallback("finish", reason + " Результат уже получен и проверен, завершаю run.");
        }

        if (state.WorkingMemory.ContainsKey("pendingPatchSet") && !state.WorkingMemory.ContainsKey("patchSetReview"))
            return AgentLoopDecision.Fallback("review_patch_set", reason + " Патчи уже подготовлены; проверяю их перед завершением.");
        if (state.WorkingMemory.ContainsKey("pendingPatchSet"))
            return AgentLoopDecision.Fallback("finish", reason + " Проверенный patch set готов, завершаю run.");

        if (!state.LoadedContext)
        {
            if (LooksLikeMassRatingEdit(state.Job.UserText))
            {
                return AgentLoopDecision.FallbackBatch(new[]
                {
                    ("inspect_context", reason + " Сначала сохраняю доступный контекст.", (JsonObject?)null),
                    ("classify_request", "Определяю, что это массовая правка курса.", null),
                    ("load_editable_assignments", "Открываю все доступные задания курса для анализа.", null),
                    ("map_course_structure", "Строю карту курса перед переразметкой рейтинга.", null),
                    ("analyze_assignment_complexity", "Оцениваю сложность каждого задания.", null),
                    ("propose_assignment_patch_set", "Готовлю patch set для рейтингов с диффами.", new JsonObject { ["operation"] = "rerate_assignments", ["field"] = "rating" }),
                    ("review_patch_set", "Проверяю patch set перед завершением.", null),
                    ("finish", "Проверенный patch set готов к показу пользователю.", null)
                });
            }
            return AgentLoopDecision.FallbackBatch(new[]
            {
                ("inspect_context", reason + " Сначала сохраняю доступный контекст в рабочую память.", (JsonObject?)null),
                ("classify_request", "После загрузки контекста нужно определить сценарий запроса.", null)
            });
        }

        if (!state.WorkingMemory.ContainsKey("intent"))
            return AgentLoopDecision.Fallback("classify_request", reason + " Нужно определить сценарий запроса.");

        var scenario = state.WorkingMemory["intent"]?["scenarioId"]?.ToString() ?? string.Empty;
        var needsCourseContext = NeedsCourseContext(state.Job.UserText) || string.Equals(state.WorkingMemory["intent"]?["needsCourseContext"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var needsDrafts = LooksLikeDraftRequest(state.Job.UserText) || scenario is "guided_ladder" or "style_matched_tasks" or "bridge_tasks" or "draft_revision";

        if (LooksLikeMassRatingEdit(state.Job.UserText))
        {
            var batch = new List<(string Action, string Reason, JsonObject? Args)>();
            if (!state.WorkingMemory.ContainsKey("editableAssignments")) batch.Add(("load_editable_assignments", reason + " Нужны все задания курса для массовой правки.", null));
            if (!state.WorkingMemory.ContainsKey("courseMap")) batch.Add(("map_course_structure", "Строю карту курса для понимания порядка и сложности.", null));
            if (!state.WorkingMemory.ContainsKey("assignmentComplexityReport")) batch.Add(("analyze_assignment_complexity", "Оцениваю сложность каждого задания.", null));
            batch.Add(("propose_assignment_patch_set", "Готовлю patch set рейтингов с причинами и диффами.", new JsonObject { ["operation"] = "rerate_assignments", ["field"] = "rating" }));
            batch.Add(("review_patch_set", "Проверяю patch set перед показом.", null));
            batch.Add(("finish", "Patch set готов.", null));
            return AgentLoopDecision.FallbackBatch(batch);
        }

        if (needsCourseContext && !state.WorkingMemory.ContainsKey("courseMap"))
            return AgentLoopDecision.Fallback("map_course_structure", reason + " Запрос зависит от курса; строю карту курса перед генерацией/анализом.");

        if ((needsCourseContext || needsDrafts) && !state.WorkingMemory.ContainsKey("courseStyleProfile"))
            return AgentLoopDecision.Fallback("extract_course_style", reason + " Нужен стиль существующих заданий, чтобы новые задания не выглядели чужими.");

        if (NeedsGapAnalysis(state.Job.UserText, scenario) && !state.WorkingMemory.ContainsKey("courseGapReport"))
            return AgentLoopDecision.Fallback("find_learning_gaps", reason + " Нужно найти пробелы и скачки сложности перед предложением улучшений.");

        if ((needsCourseContext || needsDrafts) && !state.WorkingMemory.ContainsKey("courseEnrichmentBrief"))
            return AgentLoopDecision.Fallback("plan_course_enrichment", reason + " Собираю единый brief: запрос, карта курса, стиль, пробелы и правила качества.");

        return scenario switch
        {
            "course_edit" => AgentLoopDecision.Fallback("delegate_course_edit", reason + " Запрос похож на правки курса."),
            "course_analysis" or "course_gap_audit" => AgentLoopDecision.Fallback("delegate_course_audit", reason + " Запрос похож на анализ курса."),
            "polish_assignment_draft" or "draft_revision" => AgentLoopDecision.Fallback("delegate_polish_assignment", reason + " Запрос похож на доработку задания."),
            "guided_ladder" or "style_matched_tasks" or "bridge_tasks" => AgentLoopDecision.Fallback("delegate_assignment_draft", reason + " Запрос похож на генерацию задач."),
            _ => LooksLikeDraftRequest(state.Job.UserText)
                ? AgentLoopDecision.Fallback("delegate_assignment_draft", reason + " Запрос содержит просьбу создать задачи.")
                : AgentLoopDecision.Fallback("answer_directly", reason + " Подготовлю обычный ответ в чат.")
        };
    }

    private static bool LooksLikeDraftRequest(string text)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        return (t.Contains("создай") || t.Contains("сгенер") || t.Contains("придум") || t.Contains("сделай") || t.Contains("накидай") || t.Contains("добавь"))
               && (t.Contains("задач") || t.Contains("задани") || t.Contains("курс"));
    }

    private static bool LooksLikeMassRatingEdit(string text)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        var edit = t.Contains("помен") || t.Contains("измени") || t.Contains("выстав") || t.Contains("простав") || t.Contains("пересч") || t.Contains("обнов");
        var all = t.Contains("всем") || t.Contains("все задания") || t.Contains("кажд") || t.Contains("по всему курсу") || t.Contains("курсе");
        var rating = t.Contains("рейтинг") || t.Contains("rating") || t.Contains("сложност") || t.Contains("difficulty");
        return edit && all && rating;
    }

    private static bool NeedsCourseContext(string text)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        return t.Contains("курс")
               || t.Contains("в стиле")
               || t.Contains("как в курсе")
               || t.Contains("похож")
               || t.Contains("пробел")
               || t.Contains("скач")
               || t.Contains("мостик")
               || t.Contains("между")
               || t.Contains("улучш")
               || t.Contains("рейтинг");
    }

    private static bool NeedsGapAnalysis(string text, string scenario)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        return scenario is "course_analysis" or "course_gap_audit"
               || t.Contains("пробел")
               || t.Contains("скач")
               || t.Contains("не хватает")
               || t.Contains("мостик")
               || t.Contains("улучш")
               || t.Contains("аудит");
    }

    private static AgentLoopDecision ParseDecision(string raw)
    {
        var clean = ExtractJson(raw);
        var node = JsonNode.Parse(clean) as JsonObject
                   ?? throw new InvalidOperationException("Agent decision response is not a JSON object.");

        var decision = new AgentLoopDecision
        {
            Action = node["action"]?.ToString() ?? "inspect_context",
            ReasonSummary = node["reasonSummary"]?.ToString() ?? node["reason"]?.ToString() ?? "Модель выбрала следующий шаг.",
            Args = node["args"] as JsonObject ?? new JsonObject()
        };

        if (node["actions"] is JsonArray actions)
        {
            foreach (var item in actions.OfType<JsonObject>())
            {
                decision.Actions.Add(new AgentLoopActionCall
                {
                    Action = item["action"]?.ToString() ?? "inspect_context",
                    ReasonSummary = item["reasonSummary"]?.ToString() ?? item["reason"]?.ToString() ?? decision.ReasonSummary,
                    Args = item["args"] as JsonObject ?? new JsonObject()
                });
            }
        }

        return decision;
    }

    private static string ExtractJson(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];

        return trimmed;
    }

    private static string Compact(string value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters) return value;
        return value[..maxCharacters] + "\n/* truncated */";
    }
}
