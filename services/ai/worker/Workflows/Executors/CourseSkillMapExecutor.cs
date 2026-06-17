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
    private readonly DirectLlmTextClient _textClient;
    private AIAgent? _agent;

    public CourseSkillMapExecutor(TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps, DirectLlmTextClient textClient)
    {
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
        _textClient = textClient;
    }

    public async Task<CourseSkillBridgeContext> ExecuteAsync(WorkflowState state, string contextPrompt, CancellationToken cancellationToken)
    {
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
        var skillMapInput = CourseSkillAnalyzer.BuildSkillMapInput(state.Job.Payload, state.UserText);
        state.Data["skillMapInput"] = skillMapInput.DeepClone();
        var skillMapContext = skillMapInput.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        try
        {
            // Each LLM substage gets a fresh short-lived session. The prompts carry
            // the complete explicit context, while reusing a huge conversation session
            // can break OpenAI-compatible providers with malformed assistant-role
            // conversions.

            // Stage 1: build a neutral semantic map of the course. This stage is
            // deliberately NOT allowed to pick an insertion point. It only studies
            // the actual assignments, examples and tests so the next stage is not
            // tempted to anchor on a tag, a single keyword, or a заранее зашитый topic.
            var semanticPrompt = $$"""
{{TaskForgeAgentPrompts.Coordinator}}

Ты не генерируешь задания и не выбираешь точку вставки.
Сначала внимательно изучи задания курса и построй нейтральную карту: что делает каждое задание, какие умения оно требует, какие умения вводит, насколько оно сложное и какие соседние задания образуют смысловые группы.

Запрос пользователя нужен только как контекст, но НЕ подгоняй карту под него:
{{state.UserText}}

Педагогические предпочтения преподавателя:
{{teacherPreferences}}

TASKS_ONLY_CONTEXT:
{{skillMapContext}}

Правила stage 1:
- Не выбирай anchor / insertBeforeAssignmentId.
- Не строй bridgePlan.
- Не опирайся на conceptHints/tags как на доказательство. Это слабые метаданные. Главные доказательства: текст задания, примеры, тесты, reference/summary, формат результата и соседние задания.
- Если задание только упоминает тему, но не требует её применения, раздели это в evidence: mentionOnly=true.
- Оценивай все темы одинаково гибко: любая тема курса должна проходить через одну и ту же схему анализа, без специальных веток под конкретную тему.

Верни строго JSON без markdown:
{
  "language": "ru|en|...",
  "courseSummary": "логика курса без выбора anchor",
  "taskMap": [
    {
      "assignmentId": "guid",
      "position": 0,
      "title": "...",
      "studentFacingSummary": "что реально должен сделать студент",
      "requiresSkills": ["..."],
      "introducesSkills": ["..."],
      "mentionsButDoesNotRequire": ["..."],
      "studentHasAfter": ["..."],
      "difficulty": 1,
      "evidence": ["краткие факты из условия/тестов/примеров"],
      "semanticGroup": "короткое имя группы"
    }
  ],
  "semanticGroups": [
    {"name":"...", "assignmentIds":["..."], "goal":"...", "startsAtAssignmentId":"guid"}
  ],
  "warnings": ["..."]
}
""";

            var semanticText = await _textClient.CompleteAsync(semanticPrompt, cancellationToken);
            state.Data["courseSemanticMapRaw"] = semanticText.Length <= 20000 ? semanticText : semanticText[..20000] + "...";

            // Stage 2: choose placement and bridge plan from the neutral map. This
            // is a separate reasoning step so the model must compare candidate
            // gaps instead of returning the first superficially matching task.
            var placementPrompt = $$"""
{{TaskForgeAgentPrompts.Coordinator}}

Теперь выбери точку вставки и bridgePlan для запроса пользователя.
Ты получаешь нейтральную карту курса из stage 1 и исходные задания. Не используй заранее зашитый предметную лестницу и не выбирай первую задачу по тегу/слову.

Запрос пользователя:
{{state.UserText}}

Педагогические предпочтения преподавателя:
{{teacherPreferences}}

COURSE_SEMANTIC_MAP_FROM_STAGE_1:
{{semanticText}}

TASKS_ONLY_CONTEXT:
{{skillMapContext}}

Что нужно сделать:
1. Сначала сформулируй targetCapability из запроса пользователя в общем виде. Это может быть любая тема программирования; не подставляй заранее известный сценарий под конкретную тему.
2. Найди в taskMap места, где targetCapability реально требуется студенту для решения, а не просто упоминается.
3. Рассмотри несколько candidate gaps: перед первым реальным требованием, перед первой группой задач на эту тему, и перед более поздним скачком сложности.
4. Выбери место, где bridge-задания реально уменьшают скачок сложности и не ломают последовательность курса.
5. Если первое упоминание темы слишком слабое или демонстрационное, НЕ выбирай его автоматически. Выбирай первую точку, где без нового умения студент реально не сможет решить задачу.
6. Если уверенность низкая — insertBeforeAssignmentId=null и пустой bridgePlan. Лучше остановиться, чем вставить не туда.
7. bridgePlan должен быть коротким и выводиться из gap-а между acquiredSkillsBeforeAnchor и targetSkillsAtAnchor. Один шаг — один маленький новый навык.
8. Не дублируй целевое anchor-задание как bridge-step. Если anchor уже просит "считать два числа и вывести сумму", bridgePlan должен закрывать предпосылки меньшими шагами (например, прочитать строку; затем преобразовать одну строку в число), а не повторять всю сумму двух чисел.
9. Если step учит простому преобразованию корректного ввода, не добавляй в introducedSkills/acceptanceCriteria `TryParse`, условия и обработку ошибок. Используй простой parse как отдельный микрошаг; валидация ввода — отдельный будущий навык.
10. Для echo/readline step временная переменная для хранения считанной строки является support-деталью реализации, а не отдельным будущим навыком. Не добавляй её в mustNotUse.
11. targetSkillsAtAnchor и bridgePlan относятся ТОЛЬКО к выбранному anchor-заданию. Не добавляй в bridgePlan навыки, которые нужны лишь для более поздних rejected candidate gaps. Если видишь полезный мост для более поздней группы, упомяни его в placementCandidates/reason, но не генерируй draft для него в текущей точке.

Верни строго валидный JSON без markdown:
{
  "language": "ru|en|...",
  "courseSummary": "кратко о логике курса",
  "studentModelSummary": "что студент умеет к выбранной точке",
  "targetCapability": "какое умение/тема запрошены пользователем",
  "courseMap": [
    {
      "assignmentId": "guid",
      "title": "...",
      "position": 0,
      "summary": "что делает задание",
      "requiresSkills": ["..."],
      "introducesSkills": ["..."],
      "studentHasAfter": ["..."],
      "difficulty": 1,
      "evidence": "почему так оценено",
      "isRelevantToRequest": true,
      "mentionOnly": false
    }
  ],
  "placementCandidates": [
    {
      "insertBeforeAssignmentId": "guid|null",
      "anchorAssignmentTitle": "...",
      "gapBefore": ["что студент уже умеет"],
      "gapAfter": ["что требуется дальше"],
      "pros": ["..."],
      "cons": ["..."],
      "confidence": 0.0,
      "decision": "chosen|rejected",
      "reason": "..."
    }
  ],
  "anchor": {
    "insertBeforeAssignmentId": "guid|null",
    "anchorAssignmentTitle": "...",
    "previousAssignmentTitle": "...",
    "confidence": 0.0,
    "reason": "почему именно эта точка, с сравнением альтернатив"
  },
  "requestedSkills": ["..."],
  "acquiredSkillsBeforeAnchor": ["..."],
  "targetSkillsAtAnchor": ["..."],
  "missingBridgeSkills": ["..."],
  "bridgePlan": [
    {
      "step": 0,
      "skillId": "stable-kebab-case-skill-id",
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

            var text = await _textClient.CompleteAsync(placementPrompt, cancellationToken);
            var bridge = CourseSkillAnalyzer.FromModelMap(state.Job.Payload, state.UserText, text, fallback);
            if (!string.Equals(bridge.Source, "llm-course-skill-map", StringComparison.OrdinalIgnoreCase))
            {
                var repairPrompt = $$"""
Ты вернул невалидный JSON для placement/bridge stage. Исправь ответ: верни один валидный JSON-объект строго по схеме, без markdown и пояснений.
Не меняй смысл, но если поле невозможно восстановить — используй пустой массив/null.

COURSE_SEMANTIC_MAP_FROM_STAGE_1:
{{semanticText}}

TASKS_ONLY_CONTEXT:
{{skillMapContext}}

Ошибка парсинга/причина fallback:
{{bridge.AnchorReason}}

Исходный ответ модели:
{{text}}
""";
                var repairedText = await _textClient.CompleteAsync(repairPrompt, cancellationToken);
                var repairedBridge = CourseSkillAnalyzer.FromModelMap(state.Job.Payload, state.UserText, repairedText, fallback);
                if (string.Equals(repairedBridge.Source, "llm-course-skill-map", StringComparison.OrdinalIgnoreCase))
                {
                    bridge = repairedBridge with
                    {
                        AnchorReason = string.IsNullOrWhiteSpace(repairedBridge.AnchorReason)
                            ? "Recovered from invalid first placement JSON."
                            : repairedBridge.AnchorReason
                    };
                }
                else
                {
                    state.Notes.Add($"Course skill map repair failed: {repairedBridge.AnchorReason}");
                }
            }
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
