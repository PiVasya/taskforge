using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed partial class DraftAuthorExecutor
{
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private readonly TaskForgeAgentOptions _options;
    private readonly DirectLlmTextClient _textClient;
    private AIAgent? _agent;

    public DraftAuthorExecutor(TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps, IOptions<TaskForgeAgentOptions> options, DirectLlmTextClient textClient)
    {
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
        _options = options.Value;
        _textClient = textClient;
    }

    public async Task<DraftSpec?> ExecuteAsync(WorkflowState state, string contextPrompt, string plan, int attempt, CancellationToken cancellationToken)
    {
        var drafts = await ExecuteManyAsync(state, contextPrompt, plan, attempt, 1, cancellationToken);
        return drafts.FirstOrDefault();
    }

    public async Task<List<DraftSpec>> ExecuteManyAsync(WorkflowState state, string contextPrompt, string plan, int attempt, int requestedCount, CancellationToken cancellationToken)
    {
        await _steps.TryReportAsync("draft", "running", attempt == 0 ? "Генерирую черновики заданий" : $"Перегенерирую черновики, попытка {attempt + 1}", plan);

        var bridge = state.CourseSkillBridge ?? CourseSkillAnalyzer.Analyze(state.Job.Payload, state.UserText);
        var count = ResolveDraftCount(requestedCount, bridge);
        var beforeAssignmentId = bridge.BeforeAssignmentId;
        var bridgeJson = bridge.ToJsonObject().ToJsonString();
        var teacherPreferences = state.TeacherPreferences.ToJsonString();
        var agentLoopMemory = ExtractPayloadProperty(state.Job.Payload, "agentLoopMemory");
        var generationContext = state.Data["skillMapInput"]?.ToJsonString()
            ?? CourseSkillAnalyzer.BuildSkillMapInput(state.Job.Payload, state.UserText).ToJsonString();
        var prompt = $$"""
{{TaskForgeAgentPrompts.DraftAuthor}}

Пользователь просит: {{state.UserText}}
План: {{plan}}

Сгенерируй {{count}} TaskForge draft-ов как JSON.
Если пользователь просит "задачки", "обучалки", "серия", "несколько" — верни массив drafts по возрастанию сложности.
Пиши title и description на языке пользователя/курса. Если пользователь пишет по-русски, title и description тоже должны быть по-русски.

Педагогическая модель вставки — COURSE_SKILL_MAP, уже построенная отдельным LLM-этапом:
{{bridgeJson}}

Педагогические предпочтения преподавателя / память агента:
{{teacherPreferences}}

Накопленная память adaptive agent loop перед генерацией. Здесь могут быть карта курса, стиль существующих заданий, найденные пробелы и courseEnrichmentBrief. Используй это как важный контекст качества, но не выводи служебные поля ученику:
{{agentLoopMemory}}

Правила генерации по COURSE_SKILL_MAP:
0. COURSE_SKILL_MAP — источник истины. Не выбирай anchor самостоятельно по ключевым словам и не переоценивай курс заново. Не используй заранее зашитую предметную лестницу; следуй только bridgePlan.
1. Если bridgePlan не пустой, сгенерируй задания строго по bridgePlan: один draft на один step, в том же порядке. Не добавляй лишние шаги сверх bridgePlan и не заменяй план своими любимыми темами.
2. В extra.assumedSkills/introducedSkills/targetSkills/missingBridgeSkills используй навыки из COURSE_SKILL_MAP. Также добавляй extra.bridgeSkillId = bridgePlan.step.skillId. Навыки могут быть человеческими названиями, но skillId должен быть стабильным идентификатором.
3. Каждое новое задание должно добавлять один маленький новый навык из bridgePlan.step.introducedSkills. Не используй навыки из mustNotUse и не добавляй темы, которых нет в bridgePlan/targetSkillsAtAnchor.
4. Первое bridge-задание должно быть проще целевого anchor-задания и не должно требовать больше одного нового умения.
5. Названия должны быть короткими student-facing названиями, без служебных префиксов вроде "Подготовка к заданию 5" и без повторения номера задания.
6. Связь с местом в курсе держи в extra/metadata, но НЕ пиши в description фразы вроде "Место в курсе", "перед Задание 5", "после List<T>".
7. Все code-token'ы в description оформляй inline-code через одиночные backticks, чтобы редактор показал фон.
8. Description — это ТОЛЬКО текст для ученика. Не выводи туда внутренние quality gates, acceptanceCriteria, mustNotUse, список запрещённых будущих тем, фразы "Требования и критерии приёма", "Программа должна использовать", "Нельзя применять", "тесты проверяют". Эти ограничения держи в extra/metadata и referenceSolution/tests.
9. Для обучающих bridge-задач description должен быть именно обучалкой, а не обычным условием. Стиль: дружелюбная вводная фраза, блок "Следуй шагам:", 3-5 нумерованных маленьких действий, пояснение каждой важной строки и финальная фраза "Запусти код и проверь...".
10. Не пиши в обучалке сухие секции олимпиадной задачи: "Формат ввода", "Формат вывода", отдельные блоки "Ввод"/"Вывод", "Критерии", "Тесты проверяют". Формат ввода объясняй одной простой фразой внутри урока, например: "Будем считать, что каждое число вводится с новой строки".
11. Обучалка должна быть предельно простой. Если микрошаг можно показать одной строкой кода — покажи одну строку. Не вводи проверки ошибок, условия, префиксы в выводе, массивы, разбор строки на части или другие будущие конструкции раньше, чем они нужны текущему step.
12. Не используй фразы "самый короткий", "короткий короткий" и не поощряй code golf. Проси понятное минимально необходимое решение.
13. Не ограничивайся сухим текстом вида "Считать X и вывести Y". Если задача должна чему-то научить, покажи ученику последовательность действий, как в маленьком туториале. Кодовые элементы в шагах обязательно пиши в `backticks`.
14. Для code-test обязательно нужны referenceSolution, минимум 2 publicTests и минимум 2 hiddenTests. Тесты должны соответствовать только тем умениям, которые уже разрешены этим step.
15. Если bridgePlan.step.mustNotUse запрещает переменные, методы, массивы, парсинг или любую другую тему — не используй её в решении и тестах; в description не перечисляй это как запрет для ученика, если преподаватель явно не попросил ограничения в видимом тексте. Важно: формулировка "сложный парсинг/регулярные выражения" запрещает Regex, Split, разбор нескольких токенов и нестандартные парсеры, но НЕ запрещает простой parse одного значения, если текущий step сам вводит чтение/преобразование одного значения.
16. Если не можешь выполнить step без будущих навыков, верни меньше drafts и объясни причину в extra.generationWarning, но не подменяй step другой темой.

Компактный контекст выбранного курса. Используй его только для стиля соседних заданий и примеров формата; не выбирай anchor заново:
{{generationContext}}

Верни строго валидный JSON без markdown:
{
  "drafts": [
    {
      "assignmentType": "code-test|test|math",
      "title": "...",
      "description": "...",
      "language": "cpp|csharp|java|javascript|pascal|python",
      "referenceSolution": "...",
      "difficulty": 1,
      "rating": 10,
      "sourceTaskIndex": 0,
      "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
      "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
      "testSpec": {"settings": {}, "questions": [{"type":"single-choice|multi-choice|fill|text", "prompt":"...", "options":[{"key":"a", "text":"..."}], "correctOptionKeys":["a"], "acceptedAnswers":["..."]}]},
      "mathSpec": {"settings": {}, "blocks": [{"kind":"info|number|expression|set|single-choice|multi-choice|order|match", "prompt":"...", "score":1, "acceptedAnswers":["..."], "options":[{"key":"a", "text":"..."}], "correctOptionKeys":["a"]}]},
      "tags": ["AI", "черновик"],
      "extra": {
        "bridgeSkillId": "same value as bridgePlan step.skillId",
        "assumedSkills": ["skills already known before this step"],
        "introducedSkills": ["exactly one main new skill for this step"],
        "targetSkills": ["skills needed by the anchor task"],
        "missingBridgeSkills": ["skills this bridge is trying to cover"],
        "skillBridgeReason": "why this draft belongs here"
      }
    }
  ]
}
""";

        var fallbackPrompt = BuildCompactDraftPrompt(state, bridgeJson, teacherPreferences, agentLoopMemory, count);
        var responseText = await TryRunAuthorPromptAsync(state, prompt, fallbackPrompt, attempt, cancellationToken);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            state.Notes.Add($"Draft author returned no usable LLM response on attempt {attempt + 1}; no invalid fallback draft will be saved.");
            return new List<DraftSpec>();
        }

        var drafts = ParseDrafts(responseText, state.Job);
        if (drafts.Count == 0)
        {
            state.Notes.Add($"Draft author response could not be parsed on attempt {attempt + 1}; no invalid fallback draft will be saved.");
            state.Data["draftGenerationError"] = new JsonObject
            {
                ["type"] = "DraftParseFailed",
                ["message"] = "LLM returned no parseable drafts JSON.",
                ["rawPreview"] = responseText.Length <= 4000 ? responseText : responseText[..4000] + "..."
            };
            await _steps.TryReportAsync("draft", "failed", "Не удалось разобрать черновики из ответа LLM", "Ответ модели не содержал валидный JSON drafts.", state.Data["draftGenerationError"]?.DeepClone());
            return new List<DraftSpec>();
        }

        drafts = AlignDraftsToBridgePlan(NormalizeDraftOrder(drafts), bridge, count);
        ApplyBatchMetadata(drafts, state.Job, bridge);
        state.Draft = drafts.FirstOrDefault();
        await _steps.TryReportAsync("draft", "completed", "Черновики заданий подготовлены", $"Черновиков: {drafts.Count}", new JsonObject
        {
            ["count"] = drafts.Count,
            ["insertBeforeAssignmentId"] = beforeAssignmentId?.ToString(),
            ["titles"] = new JsonArray(drafts.Select(d => JsonValue.Create(d.Title)).ToArray<JsonNode?>())
        });
        return drafts;
    }


    public async Task<DraftSpec?> RepairDraftAsync(WorkflowState state, DraftSpec original, JsonObject validation, JsonObject critique, int repairAttempt, CancellationToken cancellationToken)
    {
        var bridge = state.CourseSkillBridge ?? CourseSkillAnalyzer.Analyze(state.Job.Payload, state.UserText);
        var bridgeJson = bridge.ToJsonObject().ToJsonString();
        var teacherPreferences = state.TeacherPreferences.ToJsonString();
        var prompt = $$"""
Ты — TaskForge draft repair agent. Исправь ОДИН черновик задания так, чтобы он прошёл проверки.
Не меняй точку вставки и не меняй педагогический step. Не придумывай новую тему.
Работай универсально для любой темы программирования: опирайся только на COURSE_SKILL_MAP и замечания проверок.

Запрос пользователя:
{{state.UserText}}

COURSE_SKILL_MAP:
{{bridgeJson}}

Педагогические правила:
{{teacherPreferences}}

Исходный draft:
{{original.ToArtifactData().ToJsonString()}}

Validation:
{{validation.ToJsonString()}}

Critique:
{{critique.ToJsonString()}}

Исправь только то, что мешает сохранить черновик:
- если нет referenceSolution — добавь рабочее решение;
- если мало publicTests/hiddenTests — добавь тесты, которые проходят referenceSolution;
- если title/description содержит служебный текст — сделай student-facing формулировку;
- если description содержит внутренний чек-лист, acceptanceCriteria, mustNotUse, "Требования и критерии приёма", "Программа должна использовать", "Нельзя применять", "тесты проверяют" — убери это из видимого текста;
- если это learning-bridge/обучалка, сделай текст именно обучающим: дружелюбная вводная, "Следуй шагам:", 3-5 маленьких шагов, пояснение каждой важной строки и финальная фраза "Запусти код и проверь...";
- не используй сухие секции "Формат ввода", "Формат вывода", отдельные блоки "Ввод"/"Вывод"; объясни формат ввода одной простой фразой внутри урока;
- не пиши "самый короткий" или "короткий короткий"; проси понятное минимально необходимое решение;
- если микрошаг можно показать одной строкой кода — сделай именно так; не добавляй условия, проверки ошибок, префиксы вывода и будущие конструкции без необходимости текущего step;
- не используй future skills из mustNotUse в решении и тестах;
- оставь extra.bridgeSkillId и bridgeStepIndex совместимыми с исходным step.

Верни строго JSON без markdown:
{
  "assignmentType": "code-test|test|math",
  "title": "...",
  "description": "...",
  "language": "cpp|csharp|java|javascript|pascal|python",
  "referenceSolution": "...",
  "difficulty": 1,
  "rating": 10,
  "sourceTaskIndex": {{original.SourceTaskIndex ?? 0}},
  "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
  "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
  "testSpec": {"settings": {}, "questions": [{"type":"single-choice|multi-choice|fill|text", "prompt":"...", "options":[{"key":"a", "text":"..."}], "correctOptionKeys":["a"], "acceptedAnswers":["..."]}]},
  "mathSpec": {"settings": {}, "blocks": [{"kind":"info|number|expression|set|single-choice|multi-choice|order|match", "prompt":"...", "score":1, "acceptedAnswers":["..."], "options":[{"key":"a", "text":"..."}], "correctOptionKeys":["a"]}]},
  "tags": ["AI", "черновик"],
  "extra": {
    "bridgeSkillId": "{{original.Extra["bridgeSkillId"]?.ToString() ?? string.Empty}}",
    "assumedSkills": [],
    "introducedSkills": [],
    "targetSkills": [],
    "missingBridgeSkills": [],
    "skillBridgeReason": "..."
  }
}
""";

        try
        {
            await _steps.TryReportAsync("draft_repair", "running", $"Исправляю черновик, попытка {repairAttempt}", original.Title);
            var responseText = await _textClient.CompleteAsync(prompt, cancellationToken);
            var repaired = ParseDrafts(responseText, state.Job).FirstOrDefault();
            if (repaired == null)
            {
                state.Notes.Add($"Draft repair attempt {repairAttempt} returned no parseable draft for '{original.Title}'.");
                await _steps.TryReportAsync("draft_repair", "failed", "Не удалось разобрать исправленный черновик", original.Title);
                return null;
            }

            repaired.SourceTaskIndex = original.SourceTaskIndex;
            if (original.Extra["bridgeStepIndex"] is not null)
                repaired.Extra["bridgeStepIndex"] = original.Extra["bridgeStepIndex"]!.DeepClone();
            if (original.Extra["bridgeSkillId"] is not null)
                repaired.Extra["bridgeSkillId"] = original.Extra["bridgeSkillId"]!.DeepClone();

            var stepIndex = ReadInt(original.Extra["bridgeStepIndex"]) ?? original.SourceTaskIndex ?? 0;
            ApplyDraftMetadata(repaired, state.Job, bridge, stepIndex);
            await _steps.TryReportAsync("draft_repair", "completed", "Черновик исправлен", repaired.Title, new JsonObject
            {
                ["sourceTaskIndex"] = repaired.SourceTaskIndex,
                ["title"] = repaired.Title
            });
            return repaired;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.Notes.Add($"Draft repair attempt {repairAttempt} failed for '{original.Title}': {ex.GetType().Name}: {ex.Message}");
            await _steps.TryReportAsync("draft_repair", "failed", "Не удалось исправить черновик", ex.Message);
            return null;
        }
    }

}
