using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Workflows;

namespace TaskForge.AiAgent.Workflows.Executors;

/// <summary>
/// Builds a course skill map with the LLM before draft generation.
/// Static analysis is now a neutral structure-only fallback; it must not choose
/// the pedagogical anchor or infer the student's skills by keyword rules.
/// </summary>
public sealed class CourseSkillMapExecutor
{
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private AIAgent? _agent;

    public CourseSkillMapExecutor(TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps)
    {
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
    }

    public async Task<CourseSkillBridgeContext> ExecuteAsync(WorkflowState state, string contextPrompt, CancellationToken cancellationToken)
    {
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        await _steps.TryReportAsync(
            "skill_map",
            "running",
            "Оцениваю карту навыков курса",
            "Агент сам строит модель: что студент уже умеет, где появляется новый навык и какие bridge-шаги нужны.");

        var fallback = CourseSkillAnalyzer.Analyze(state.Job.Payload, state.UserText) with
        {
            Source = "neutral-fallback",
            AnchorReason = "Fallback only lists course structure; it does not infer skills or insertion anchors."
        };
        var teacherPreferences = state.TeacherPreferences.ToJsonString();
        var prompt = $$"""
{{TaskForgeAgentPrompts.Coordinator}}

Задача: оцени курс как педагог и верни COURSE_SKILL_MAP для последующей генерации заданий.
Это главный reasoning-этап: именно ты строишь модель навыков студента, а не backend-эвристики.

Запрос пользователя:
{{state.UserText}}

Педагогические предпочтения преподавателя / память агента:
{{teacherPreferences}}

Нужно НЕ генерировать задания. Нужно только понять структуру курса:
1. Какие задания уже идут до точки вставки.
2. Какие навыки студент уже должен иметь после каждого задания.
3. В каком задании впервые появляется новый навык из запроса пользователя.
4. Какие маленькие bridge-шаги нужны перед этой точкой.

Правила:
- Не выбирай anchor по одному слову, regex или совпадению букв. Смотри на смысл задания, условие, тесты, название и соседей.
- Backend может дать контекст и fallback, но источником истины является твоя COURSE_SKILL_MAP.
- Не путай похожие по написанию термины; оценивай действие задания по смыслу, формату ввода/вывода и тестам.
- Если в контексте есть мусорные/чужие соседние элементы, укажи их в warnings и не используй как основу для педагогического мостика.
- Используй только assignmentId из контекста. Если не уверен в точке вставки, поставь insertBeforeAssignmentId = null и объясни reason.
- Bridge-план должен быть настолько коротким, насколько нужно. Не надо делать 5 шагов, если достаточно 2-3.
- Каждый bridge-шаг вводит один маленький новый навык и не использует будущие навыки.
- Если пользователь просит обучалки/мостик, сначала явно опиши acquiredSkillsBeforeAnchor и targetSkillsAtAnchor, потом missingBridgeSkills, потом bridgePlan.
- Если курс уже содержит нужные подготовительные задания или точка вставки не ясна, верни пустой bridgePlan и предупреждение; не притягивай задания силой.

Контекст TaskForge:
{{contextPrompt}}

Верни строго JSON без markdown:
{
  "language": "ru|en|...",
  "courseSummary": "кратко о логике курса",
  "studentModelSummary": "что студент реально умеет к выбранной точке",
  "courseMap": [
    {
      "assignmentId": "guid",
      "title": "...",
      "position": 1,
      "summary": "что делает задание",
      "requiresSkills": ["..."],
      "introducesSkills": ["..."],
      "studentHasAfter": ["..."],
      "difficulty": 1,
      "evidence": "какие слова/тесты/условие доказывают этот вывод",
      "isRelevantToRequest": true
    }
  ],
  "anchor": {
    "insertBeforeAssignmentId": "guid|null",
    "anchorAssignmentTitle": "...",
    "previousAssignmentTitle": "...",
    "reason": "почему именно эта точка"
  },
  "requestedSkills": ["..."],
  "acquiredSkillsBeforeAnchor": ["..."],
  "targetSkillsAtAnchor": ["..."],
  "missingBridgeSkills": ["..."],
  "bridgePlan": [
    {
      "step": 0,
      "titleHint": "короткое student-facing название",
      "assumedSkills": ["что уже можно использовать"],
      "introducedSkills": ["ровно один главный новый навык"],
      "mustNotUse": ["что ещё нельзя использовать"],
      "acceptanceCriteria": ["как понять, что step закрыт"],
      "reason": "зачем этот шаг нужен перед anchor"
    }
  ],
  "warnings": ["..."]
}
""";

        try
        {
            var session = await _sessionStore.LoadAsync(_agent, state.Job.ConversationId, cancellationToken);
            var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);
            var text = response.Text ?? string.Empty;
            var bridge = CourseSkillAnalyzer.FromModelMap(state.Job.Payload, state.UserText, text, fallback);
            state.CourseSkillBridge = bridge;
            state.Data["courseSkillMap"] = bridge.ToJsonObject();
            AddSkillMapArtifact(state, bridge);
            await _steps.TryReportAsync("skill_map", "completed", "Карта навыков курса построена", bridge.AnchorTitle == null ? "Точка вставки не определена моделью." : $"Точка вставки: перед {bridge.AnchorTitle}", bridge.ToJsonObject());
            return bridge;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.Notes.Add($"Course skill map LLM call failed: {ex.GetType().Name}: {ex.Message}. Neutral fallback used; bridge draft generation should stop.");
            var bridge = fallback with
            {
                Source = "llm-course-skill-map-failed",
                AnchorReason = "LLM course skill map failed; neutral structure-only fallback was used. Draft generation should stop for bridge requests."
            };
            state.CourseSkillBridge = bridge;
            state.Data["courseSkillMap"] = bridge.ToJsonObject();
            AddSkillMapArtifact(state, bridge);
            await _steps.TryReportAsync("skill_map", "failed", "Не удалось уверенно построить карту навыков", ex.Message, bridge.ToJsonObject());
            return bridge;
        }
    }

    private static void AddSkillMapArtifact(WorkflowState state, CourseSkillBridgeContext bridge)
    {
        var data = bridge.ToJsonObject();
        data["teacherPreferences"] = state.TeacherPreferences.DeepClone();
        state.Artifacts.Add(new AgentArtifact(
            "course_skill_map_ready",
            "Карта навыков курса",
            data));
    }
}
