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

public sealed class DraftAuthorExecutor
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

    public async Task<DraftSpec> ExecuteAsync(WorkflowState state, string contextPrompt, string plan, int attempt, CancellationToken cancellationToken)
    {
        var drafts = await ExecuteManyAsync(state, contextPrompt, plan, attempt, 1, cancellationToken);
        return drafts.FirstOrDefault() ?? BuildFallbackDraft(state.Job, attempt, string.Empty);
    }

    public async Task<List<DraftSpec>> ExecuteManyAsync(WorkflowState state, string contextPrompt, string plan, int attempt, int requestedCount, CancellationToken cancellationToken)
    {
        await _steps.TryReportAsync("draft", "running", attempt == 0 ? "Генерирую черновики заданий" : $"Перегенерирую черновики, попытка {attempt + 1}", plan);

        var bridge = state.CourseSkillBridge ?? CourseSkillAnalyzer.Analyze(state.Job.Payload, state.UserText);
        var count = ResolveDraftCount(requestedCount, bridge);
        var beforeAssignmentId = bridge.BeforeAssignmentId;
        var bridgeJson = bridge.ToJsonObject().ToJsonString();
        var teacherPreferences = state.TeacherPreferences.ToJsonString();
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
9. Для обучающих bridge-задач description должен быть именно обучалкой, а не обычным условием. Стиль: дружелюбная вводная фраза, блок "Следуй шагам:", 3-5 нумерованных маленьких действий, короткое пояснение зачем это делается, мини-проверка на примере. Пример тона: "Давай научимся...", "Напиши...", "Запусти и проверь...".
10. Обучалка должна быть предельно простой. Если микрошаг можно показать одной строкой кода — покажи одну строку. Не вводи переменные, проверки ошибок, `TryParse`, условия, префиксы в выводе, массивы или `Split()` раньше, чем они нужны текущему step.
11. Не ограничивайся сухим текстом вида "Считать X и вывести Y". Если задача должна чему-то научить, покажи ученику последовательность действий, как в маленьком туториале. Кодовые элементы в шагах обязательно пиши в `backticks`.
12. Для code-test обязательно нужны referenceSolution, минимум 2 publicTests и минимум 2 hiddenTests. Тесты должны соответствовать только тем умениям, которые уже разрешены этим step.
13. Если bridgePlan.step.mustNotUse запрещает переменные, методы, массивы, парсинг или любую другую тему — не используй её в решении и тестах; в description не перечисляй это как запрет для ученика, если преподаватель явно не попросил ограничения в видимом тексте.
14. Если не можешь выполнить step без будущих навыков, верни меньше drafts и объясни причину в extra.generationWarning, но не подменяй step другой темой.

Компактный контекст выбранного курса. Используй его только для стиля соседних заданий и примеров формата; не выбирай anchor заново:
{{generationContext}}

Верни строго валидный JSON без markdown:
{
  "drafts": [
    {
      "assignmentType": "code-test|test|math",
      "title": "...",
      "description": "...",
      "language": "cpp|csharp|java|python|javascript|pascal",
      "referenceSolution": "...",
      "difficulty": 1,
      "rating": 10,
      "sourceTaskIndex": 0,
      "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
      "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
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

        var fallbackPrompt = BuildCompactDraftPrompt(state, bridgeJson, teacherPreferences, count);
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

        drafts = NormalizeDraftOrder(drafts).Take(count).ToList();
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
- если это learning-bridge/обучалка, сделай текст именно обучающим: дружелюбная вводная, "Следуй шагам:", 3-5 маленьких шагов, короткое пояснение и мини-проверка на примере;
- если микрошаг можно показать одной строкой кода — сделай именно так; не добавляй переменные, `TryParse`, условия, проверки ошибок и префиксы вывода без необходимости текущего step;
- если не хватает формата ввода/вывода — добавь его через понятный пример, а не сухой чек-лист;
- не используй future skills из mustNotUse в решении и тестах;
- оставь extra.bridgeSkillId и bridgeStepIndex совместимыми с исходным step.

Верни строго JSON без markdown:
{
  "assignmentType": "code-test|test|math",
  "title": "...",
  "description": "...",
  "language": "cpp|csharp|java|python|javascript|pascal",
  "referenceSolution": "...",
  "difficulty": 1,
  "rating": 10,
  "sourceTaskIndex": {{original.SourceTaskIndex ?? 0}},
  "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
  "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
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

    private static int ResolveDraftCount(int requestedCount, CourseSkillBridgeContext bridge)
    {
        var requested = Math.Clamp(requestedCount, 1, 6);
        var planned = bridge.BridgePlan?.Count ?? 0;
        if (planned > 0) return Math.Clamp(planned, 1, Math.Min(6, requested));
        return requested;
    }

    private async Task<string?> TryRunAuthorPromptAsync(WorkflowState state, string primaryPrompt, string fallbackPrompt, int attempt, CancellationToken cancellationToken)
    {
        var errors = new JsonArray();

        async Task<string?> TryOneAsync(string label, string candidatePrompt, int retry)
        {
            try
            {
                // Tool-less draft generation goes through the direct chat-completions
                // client. It avoids Microsoft.Agents.AI session/history conversion
                // failures seen with some OpenAI-compatible providers.
                var text = await _textClient.CompleteAsync(candidatePrompt, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text)) return text;

                errors.Add(new JsonObject
                {
                    ["prompt"] = label,
                    ["retry"] = retry,
                    ["type"] = "EmptyResponse",
                    ["message"] = "LLM response text was empty."
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add(new JsonObject
                {
                    ["prompt"] = label,
                    ["retry"] = retry,
                    ["type"] = ex.GetType().Name,
                    ["message"] = ex.Message
                });
            }

            return null;
        }

        foreach (var (label, candidatePrompt) in new[] { ("primary", primaryPrompt), ("compact", fallbackPrompt) })
        {
            for (var retry = 0; retry < 2; retry++)
            {
                var text = await TryOneAsync(label, candidatePrompt, retry);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        state.Data["draftAuthorErrors"] = errors;
        state.Data["draftGenerationError"] = new JsonObject
        {
            ["type"] = "DraftAuthorUnavailable",
            ["message"] = "All draft-author LLM attempts failed.",
            ["attempt"] = attempt + 1,
            ["errors"] = errors.DeepClone()
        };
        await _steps.TryReportAsync("draft", "failed", "Не удалось получить ответ LLM для черновиков", "Все попытки draft-author завершились ошибкой; не создаю невалидный fallback-черновик.", state.Data["draftGenerationError"]?.DeepClone());
        return null;
    }

    private static string BuildCompactDraftPrompt(WorkflowState state, string bridgeJson, string teacherPreferences, int count)
    {
        return $$"""
Ты — TaskForge draft author. Сгенерируй до {{count}} учебных draft-ов строго по COURSE_SKILL_MAP.bridgePlan.
Не выбирай место вставки и не переоценивай курс: placement уже решён отдельной стадией.
Тема может быть любой темой программирования; не используй зашитые предметные лестницы.

Запрос пользователя:
{{state.UserText}}

COURSE_SKILL_MAP:
{{bridgeJson}}

Педагогические правила:
{{teacherPreferences}}

Требования:
- Один draft на один bridgePlan step, в том же порядке.
- Один главный новый навык на draft.
- Не использовать mustNotUse текущего step.
- Student-facing title/description, без служебной metadata.
- Description не должен содержать внутренние требования валидатора, acceptanceCriteria, mustNotUse, списки запретов и фразы вроде "Требования и критерии приёма", "Программа должна использовать", "Нельзя применять".
- Пиши learning-bridge как маленький туториал: дружелюбная вводная, "Следуй шагам:", 3-5 нумерованных действий, мини-проверка на примере. Не пиши сухое "Считать X и вывести Y" без обучения.
- Делай обучалку максимально простой: если можно одной строкой кода — используй одну строку. Не добавляй переменные, `TryParse`, условия, проверки ошибок, префиксы вывода или будущие темы без необходимости текущего step.
- Если это code-test, дай referenceSolution, 2 publicTests и 2 hiddenTests, которые проходят решение.
- Если для какого-то step невозможно дать корректный draft с тестами, просто пропусти этот step, не выдумывай fallback.

Верни только валидный JSON без markdown:
{
  "drafts": [
    {
      "assignmentType": "code-test",
      "title": "...",
      "description": "...",
      "language": "csharp",
      "referenceSolution": "...",
      "difficulty": 1,
      "rating": 10,
      "sourceTaskIndex": 0,
      "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
      "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
      "tags": ["AI", "черновик"],
      "extra": {
        "bridgeSkillId": "same as bridgePlan step.skillId",
        "assumedSkills": [],
        "introducedSkills": [],
        "targetSkills": [],
        "missingBridgeSkills": [],
        "skillBridgeReason": "..."
      }
    }
  ]
}
""";
    }

    private List<DraftSpec> ParseDrafts(string text, ClaimedAgentJob job)
    {
        var result = new List<DraftSpec>();
        try
        {
            var json = ExtractJson(text);
            if (json == null) return result;
            var root = JsonNode.Parse(json);
            if (root is JsonArray arr)
            {
                foreach (var item in arr.OfType<JsonObject>())
                    result.Add(ParseDraftObject(item, job, text));
            }
            else if (root is JsonObject obj)
            {
                if (obj["drafts"] is JsonArray drafts)
                {
                    foreach (var item in drafts.OfType<JsonObject>())
                        result.Add(ParseDraftObject(item, job, text));
                }
                else
                {
                    result.Add(ParseDraftObject(obj, job, text));
                }
            }
        }
        catch
        {
            // fallback below
        }
        return result.Where(IsDraftUseful).ToList();
    }

    private DraftSpec ParseDraftObject(JsonObject node, ClaimedAgentJob job, string rawText)
    {
        var extra = BuildDraftExtra(node, rawText);
        return new DraftSpec
        {
            AssignmentType = node["assignmentType"]?.ToString() ?? node["assignment_type"]?.ToString() ?? "code-test",
            Title = node["title"]?.ToString() ?? "AI-задание",
            Description = node["description"]?.ToString() ?? node["condition"]?.ToString() ?? "Описание задания не было заполнено моделью.",
            Language = NormalizeLanguage(node["language"]?.ToString() ?? _options.DefaultLanguage),
            ReferenceSolution = node["referenceSolution"]?.ToString() ?? node["solution"]?.ToString() ?? string.Empty,
            Difficulty = int.TryParse(node["difficulty"]?.ToString(), out var d) ? Math.Clamp(d, 1, 3) : 1,
            Rating = int.TryParse(node["rating"]?.ToString(), out var r) ? Math.Max(1, r) : 10,
            CourseId = job.CourseId,
            SourceTaskIndex = int.TryParse(node["sourceTaskIndex"]?.ToString() ?? node["source_task_index"]?.ToString(), out var idx) ? idx : null,
            Tags = ReadStringArray(node["tags"]).DefaultIfEmpty("AI").ToList(),
            PublicTests = ReadTests(node["publicTests"] ?? node["tests"], false),
            HiddenTests = ReadTests(node["hiddenTests"], true),
            Extra = extra
        };
    }

    private static JsonObject BuildDraftExtra(JsonObject node, string rawText)
    {
        JsonObject extra;
        if (node["extra"] is JsonObject modelExtra)
            extra = modelExtra.DeepClone() as JsonObject ?? new JsonObject();
        else
            extra = new JsonObject();

        CopySkillArray(node, extra, "assumedSkills");
        CopySkillArray(node, extra, "introducedSkills");
        CopySkillArray(node, extra, "targetSkills");
        CopySkillArray(node, extra, "missingBridgeSkills");
        CopySkillArray(node, extra, "missingSkills", "missingBridgeSkills");

        if (node["skillBridgeReason"] is not null && extra["skillBridgeReason"] is null)
            extra["skillBridgeReason"] = node["skillBridgeReason"]!.ToString();

        extra["rawModelDraft"] = rawText.Length > 6000 ? rawText[..6000] : rawText;
        return extra;
    }

    private static void CopySkillArray(JsonObject source, JsonObject target, string sourceName, string? targetName = null)
    {
        targetName ??= sourceName;
        if (target[targetName] is not null || source[sourceName] is null) return;
        var values = ReadStringArray(source[sourceName]).ToList();
        if (values.Count == 0) return;
        var arr = new JsonArray();
        foreach (var value in values) arr.Add(value);
        target[targetName] = arr;
    }

    private static bool IsDraftUseful(DraftSpec draft)
        => !string.IsNullOrWhiteSpace(draft.Title) && !string.IsNullOrWhiteSpace(draft.Description);

    private DraftSpec BuildFallbackDraft(ClaimedAgentJob job, int attempt, string text)
    {
        return new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "AI-черновик задания",
            Description = $"Подготовить задание по запросу: {job.UserText}",
            Language = NormalizeLanguage(_options.DefaultLanguage),
            Difficulty = 1,
            Rating = 10,
            CourseId = job.CourseId,
            SourceTaskIndex = attempt,
            Tags = new List<string> { "AI", "черновик", "needs-review" },
            Extra = new JsonObject { ["parseFallback"] = true, ["rawModelText"] = text.Length > 6000 ? text[..6000] : text }
        };
    }

    private static void ApplyBatchMetadata(List<DraftSpec> drafts, ClaimedAgentJob job, CourseSkillBridgeContext bridge)
    {
        for (var i = 0; i < drafts.Count; i++)
        {
            var stepIndex = bridge.BridgePlan is { Count: > 0 } ? i : drafts[i].SourceTaskIndex ?? i;
            ApplyDraftMetadata(drafts[i], job, bridge, stepIndex);
        }
    }

    private static void ApplyDraftMetadata(DraftSpec draft, ClaimedAgentJob job, CourseSkillBridgeContext bridge, int stepIndex)
    {
        draft.CourseId = job.CourseId;
        draft.BeforeAssignmentId ??= bridge.BeforeAssignmentId;
        if (bridge.BridgePlan is { Count: > 0 })
            draft.SourceTaskIndex = stepIndex;
        else
            draft.SourceTaskIndex ??= stepIndex;

        // Public tags stay clean. Learning-bridge and skill details are kept in Extra metadata, not in course cards.
        var requiredTags = new List<string> { "AI", "черновик" };
        draft.Tags = BuildPublicTags(MergeTags(draft.Tags, requiredTags));
        draft.Language = NormalizeLanguage(draft.Language);
        draft.Title = SanitizeStudentFacingTitle(draft.Title);
        draft.Difficulty = Math.Clamp(draft.Difficulty, 1, 3);
        draft.Rating = Math.Max(1, draft.Rating);
        AddBridgeExtra(draft, bridge, stepIndex);
        draft.Description = SanitizeStudentFacingDescription(draft.Description);

        // For bridge tasks we do not trust a "tutorial-looking" LLM text blindly.
        // The model often writes a friendly lesson, but still sneaks in a prefix,
        // variables, TryParse/validation, or other future concepts. For the first
        // C# input micro-steps, normalize the whole draft to the smallest runnable
        // exercise; otherwise fall back to the generic tutorial shaper.
        if (!TryNormalizeSimpleCSharpLearningBridgeDraft(draft, bridge, stepIndex))
            draft.Description = EnsureLearningBridgeTutorialStyle(draft, bridge, stepIndex);
    }

    private static void AddBridgeExtra(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var plannedStep = bridge.BridgePlan != null && stepIndex >= 0 && stepIndex < bridge.BridgePlan.Count
            ? bridge.BridgePlan[stepIndex]
            : null;

        draft.Extra["bridgeStepIndex"] = stepIndex;
        if (plannedStep is not null)
        {
            if (draft.Extra["introducedSkills"] is not null && draft.Extra["modelIntroducedSkills"] is null)
                draft.Extra["modelIntroducedSkills"] = draft.Extra["introducedSkills"]!.DeepClone();

            draft.Extra["courseSkillMapStep"] = plannedStep.DeepClone();
            draft.Extra["assumedSkills"] = plannedStep["assumedSkills"]?.DeepClone();
            draft.Extra["introducedSkills"] = plannedStep["introducedSkills"]?.DeepClone();
            draft.Extra["skillBridgeReason"] = plannedStep["reason"]?.DeepClone();

            var skillId = plannedStep["skillId"]?.ToString();
            if (string.IsNullOrWhiteSpace(skillId))
                skillId = CourseSkillAnalyzer.NormalizeSkillId(string.Join(" ", ReadStringArray(plannedStep["introducedSkills"])));
            if (!string.IsNullOrWhiteSpace(skillId))
            {
                draft.Extra["bridgeSkillId"] = skillId;
                draft.Extra["introducedSkillIds"] = ToJsonArray(new[] { skillId });
            }
        }
        draft.Extra["insertBeforeAssignmentId"] ??= bridge.BeforeAssignmentId?.ToString();
        draft.Extra["previousAssignmentTitle"] ??= bridge.PreviousTitle;
        draft.Extra["anchorAssignmentTitle"] ??= bridge.AnchorTitle;
        draft.Extra["requestedSkills"] ??= ToJsonArray(bridge.RequestedSkills);
        draft.Extra["acquiredSkillsBeforeAnchor"] ??= ToJsonArray(bridge.AcquiredSkills);
        draft.Extra["targetSkills"] ??= ToJsonArray(bridge.TargetSkills);
        draft.Extra["missingBridgeSkills"] ??= ToJsonArray(bridge.MissingBridgeSkills);
        draft.Extra["skillBridge"] ??= bridge.ToJsonObject();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            arr.Add(value);
        return arr;
    }


    private static string EnsureLearningBridgeTutorialStyle(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var clean = (draft.Description ?? string.Empty).Trim();
        if (!LooksLikeLearningBridgeDraft(draft, bridge))
            return clean;

        var language = (draft.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (language == "csharp" || language == "cs" || language == "c#")
        {
            var kind = DetectCSharpBridgeTutorialKind(draft, bridge, stepIndex);
            if (kind == CSharpBridgeTutorialKind.Split)
                return BuildCSharpSplitTutorialDescription(draft);
            if (kind == CSharpBridgeTutorialKind.IntParse)
                return BuildCSharpIntParseTutorialDescription(draft);
            if (kind == CSharpBridgeTutorialKind.ReadLine)
                return BuildCSharpReadLineTutorialDescription(draft);
        }

        if (LooksLikeTutorialStyle(clean))
            return WrapKnownCodeTokens(CollapseBlankLines(clean));

        return EnsureGenericTutorialShape(clean, draft);
    }

    private enum CSharpBridgeTutorialKind
    {
        ReadLine,
        IntParse,
        Split
    }

    private static CSharpBridgeTutorialKind? DetectCSharpBridgeTutorialKind(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        // Prefer the current COURSE_SKILL_MAP step. Title/description/reference solution are
        // model-produced and may already contain future concepts, so they are only a fallback.
        var stepText = CollectPlannedStepText(draft, bridge, stepIndex).ToLowerInvariant();
        if (ContainsAny(stepText, "split", "раздел", "разбить", "несколько знач", "двумя числами", "два числа из одной строки"))
            return CSharpBridgeTutorialKind.Split;
        if (ContainsAny(stepText, "parse", "convert.toint32", "int.parse", "целое число", "число", "int", "парсинг"))
            return CSharpBridgeTutorialKind.IntParse;
        if (ContainsAny(stepText, "readline", "console.readline", "строк", "ввод"))
            return CSharpBridgeTutorialKind.ReadLine;

        var fallback = CollectSkillText(draft, bridge, stepIndex).ToLowerInvariant();
        if (ContainsAny(fallback, "split", "раздел", "разбить", "несколько знач", "двумя числами", "два числа из одной строки"))
            return CSharpBridgeTutorialKind.Split;
        if (ContainsAny(fallback, "parse", "convert.toint32", "int.parse", "целое число", "число", "int", "парсинг"))
            return CSharpBridgeTutorialKind.IntParse;
        if (ContainsAny(fallback, "readline", "console.readline", "строк", "ввод"))
            return CSharpBridgeTutorialKind.ReadLine;

        return null;
    }

    private static string CollectPlannedStepText(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var parts = new List<string>
        {
            draft.Extra["bridgeSkillId"]?.ToString() ?? string.Empty
        };

        if (bridge.BridgePlan != null && stepIndex >= 0 && stepIndex < bridge.BridgePlan.Count)
        {
            var step = bridge.BridgePlan[stepIndex];
            parts.Add(step["skillId"]?.ToString() ?? string.Empty);
            parts.Add(step["titleHint"]?.ToString() ?? string.Empty);
            parts.Add(step["reason"]?.ToString() ?? string.Empty);
            AddJsonText(parts, step["introducedSkills"]);
            AddJsonText(parts, step["introducedSkillIds"]);
            AddJsonText(parts, step["assumedSkills"]);
        }

        AddJsonText(parts, draft.Extra["introducedSkills"]);
        AddJsonText(parts, draft.Extra["introducedSkillIds"]);
        return string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static bool TryNormalizeSimpleCSharpLearningBridgeDraft(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        if (!LooksLikeLearningBridgeDraft(draft, bridge))
            return false;

        var language = (draft.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (language != "csharp" && language != "cs" && language != "c#")
            return false;

        var kind = DetectCSharpBridgeTutorialKind(draft, bridge, stepIndex);
        if (kind == null)
            return false;

        draft.Language = "csharp";
        switch (kind.Value)
        {
            case CSharpBridgeTutorialKind.ReadLine:
                ApplyCSharpReadLineMicroDraft(draft);
                break;
            case CSharpBridgeTutorialKind.IntParse:
                ApplyCSharpIntParseMicroDraft(draft);
                break;
            case CSharpBridgeTutorialKind.Split:
                ApplyCSharpSplitMicroDraft(draft);
                break;
        }

        return true;
    }

    private static void ApplyCSharpReadLineMicroDraft(DraftSpec draft)
    {
        draft.Title = "Повторить введённую строку";
        draft.Description = BuildCSharpReadLineTutorialDescription(draft);
        draft.ReferenceSolution = """
using System;

class Program
{
    static void Main()
    {
        Console.WriteLine(Console.ReadLine());
    }
}
""".Trim();
        draft.PublicTests = new List<TestCaseSpec>
        {
            new() { Input = "Hello", ExpectedOutput = "Hello", IsHidden = false },
            new() { Input = "Привет", ExpectedOutput = "Привет", IsHidden = false }
        };
        draft.HiddenTests = new List<TestCaseSpec>
        {
            new() { Input = "C#", ExpectedOutput = "C#", IsHidden = true },
            new() { Input = "123", ExpectedOutput = "123", IsHidden = true }
        };
        SetBridgeSkillMetadata(draft,
            "console-input-line",
            "использовать `Console.ReadLine()` как значение внутри вывода",
            "Самый маленький шаг: без переменных, парсинга, условий и лишнего текста.");
    }

    private static void ApplyCSharpIntParseMicroDraft(DraftSpec draft)
    {
        draft.Title = "Прочитать целое число";
        draft.Description = BuildCSharpIntParseTutorialDescription(draft);
        draft.ReferenceSolution = """
using System;

class Program
{
    static void Main()
    {
        Console.WriteLine(int.Parse(Console.ReadLine()));
    }
}
""".Trim();
        draft.PublicTests = new List<TestCaseSpec>
        {
            new() { Input = "7", ExpectedOutput = "7", IsHidden = false },
            new() { Input = "-3", ExpectedOutput = "-3", IsHidden = false }
        };
        draft.HiddenTests = new List<TestCaseSpec>
        {
            new() { Input = "0", ExpectedOutput = "0", IsHidden = true },
            new() { Input = "42", ExpectedOutput = "42", IsHidden = true }
        };
        SetBridgeSkillMetadata(draft,
            "parse-int",
            "преобразовать результат `Console.ReadLine()` в `int` через `int.Parse(...)`",
            "Без проверки ошибок, условий, `TryParse`, массивов и `Split()` — только первый шаг к числовому вводу.");
    }

    private static void ApplyCSharpSplitMicroDraft(DraftSpec draft)
    {
        draft.Title = "Два числа из одной строки";
        draft.Description = BuildCSharpSplitTutorialDescription(draft);
        draft.ReferenceSolution = """
using System;

class Program
{
    static void Main()
    {
        string[] parts = Console.ReadLine().Split(' ');
        Console.WriteLine(int.Parse(parts[0]) + int.Parse(parts[1]));
    }
}
""".Trim();
        draft.PublicTests = new List<TestCaseSpec>
        {
            new() { Input = "3 4", ExpectedOutput = "7", IsHidden = false },
            new() { Input = "-2 5", ExpectedOutput = "3", IsHidden = false }
        };
        draft.HiddenTests = new List<TestCaseSpec>
        {
            new() { Input = "0 0", ExpectedOutput = "0", IsHidden = true },
            new() { Input = "10 -3", ExpectedOutput = "7", IsHidden = true }
        };
        SetBridgeSkillMetadata(draft,
            "split-input",
            "разделить одну строку на части через `Split(' ')`",
            "Микрошаг после одиночного числового ввода: только два числа, без циклов, LINQ и коллекций.");
    }

    private static void SetBridgeSkillMetadata(DraftSpec draft, string skillId, string introducedSkill, string reason)
    {
        draft.Extra["bridgeSkillId"] = skillId;
        draft.Extra["introducedSkillIds"] = ToJsonArray(new[] { skillId });
        draft.Extra["introducedSkills"] = ToJsonArray(new[] { introducedSkill });
        draft.Extra["skillBridgeReason"] = reason;
    }

    private static bool LooksLikeLearningBridgeDraft(DraftSpec draft, CourseSkillBridgeContext bridge)
    {
        if (bridge.BridgePlan is { Count: > 0 }) return true;
        if (draft.Extra["courseSkillMapStep"] is not null) return true;
        if (draft.Extra["bridgeSkillId"] is not null) return true;
        if (draft.Extra["skillBridge"] is not null) return true;
        return false;
    }

    private static bool LooksLikeTutorialStyle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        var hasSteps = lower.Contains("следуй шагам") || lower.Contains("шаг 1") || Regex.IsMatch(lower, @"(^|\n)\s*1\.\s+", RegexOptions.CultureInvariant);
        var hasTeachingTone = lower.Contains("давай") || lower.Contains("научимся") || lower.Contains("запусти") || lower.Contains("проверь");
        return hasSteps && hasTeachingTone;
    }

    private static string CollectSkillText(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var parts = new List<string>
        {
            draft.Title ?? string.Empty,
            draft.Description ?? string.Empty,
            draft.ReferenceSolution ?? string.Empty,
            draft.Extra["bridgeSkillId"]?.ToString() ?? string.Empty,
            draft.Extra["skillBridgeReason"]?.ToString() ?? string.Empty
        };

        // Classify the CURRENT bridge step only. Do not read targetSkills/mustNotUse here:
        // those often describe future skills and can make a ReadLine tutorial look like a Parse/Split task.
        AddJsonText(parts, draft.Extra["introducedSkills"]);
        AddJsonText(parts, draft.Extra["assumedSkills"]);

        if (bridge.BridgePlan != null && stepIndex >= 0 && stepIndex < bridge.BridgePlan.Count)
        {
            var step = bridge.BridgePlan[stepIndex];
            parts.Add(step["skillId"]?.ToString() ?? string.Empty);
            parts.Add(step["titleHint"]?.ToString() ?? string.Empty);
            parts.Add(step["reason"]?.ToString() ?? string.Empty);
            AddJsonText(parts, step["introducedSkills"]);
            AddJsonText(parts, step["introducedSkillIds"]);
            AddJsonText(parts, step["assumedSkills"]);
        }

        return string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static void AddJsonText(List<string> parts, JsonNode? node)
    {
        if (node == null) return;
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (!string.IsNullOrWhiteSpace(item?.ToString())) parts.Add(item!.ToString());
            return;
        }
        parts.Add(node.ToJsonString());
    }

    private static bool ContainsAny(string text, params string[] fragments)
        => fragments.Any(fragment => text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string BuildCSharpReadLineTutorialDescription(DraftSpec draft)
    {
        var sample = FirstPublicTestInput(draft, "Hello");
        var expected = FirstPublicTestOutput(draft, sample);
        var text = $$"""
Давай сделаем самый маленький шаг: программа сразу выведет то, что пользователь ввёл.

Следуй шагам:
1. Внутри `Main` напиши одну строку: `Console.WriteLine(Console.ReadLine());`
2. Запусти программу.
3. Введи `{{sample}}` и нажми `Enter`.
4. Проверь: программа вывела `{{expected}}`.

Что происходит: `Console.ReadLine()` ждёт ввод с клавиатуры, а `Console.WriteLine(...)` сразу печатает полученное значение.

Пока не нужны переменные, числа, `int.Parse(...)`, условия или `Split(...)`.
""";
        return WrapKnownCodeTokens(CollapseBlankLines(text.Trim()));
    }

    private static string BuildCSharpIntParseTutorialDescription(DraftSpec draft)
    {
        var sample = FirstPublicTestInput(draft, "7");
        var expected = FirstPublicTestOutput(draft, sample.Trim());

        var text = $$"""
Давай сделаем следующий маленький шаг: прочитаем число и выведем его обратно.

Следуй шагам:
1. Внутри `Main` напиши одну строку: `Console.WriteLine(int.Parse(Console.ReadLine()));`
2. Запусти программу.
3. Введи `{{sample}}` и нажми `Enter`.
4. Проверь: программа вывела `{{expected}}`.

Что происходит: `Console.ReadLine()` получает текст, `int.Parse(...)` превращает этот текст в целое число, а `Console.WriteLine(...)` печатает число.

Пока вводим только корректное целое число. Не нужны условия, `TryParse`, массивы или `Split(...)`.
""";
        return WrapKnownCodeTokens(CollapseBlankLines(text.Trim()));
    }

    private static string BuildCSharpSplitTutorialDescription(DraftSpec draft)
    {
        var sample = FirstPublicTestInput(draft, "3 4");
        var expected = FirstPublicTestOutput(draft, "7");
        var text = $$"""
Давай сделаем маленький шаг: прочитаем два числа из одной строки.

Следуй шагам:
1. Считай строку и сразу раздели её по пробелу: `string[] parts = Console.ReadLine().Split(' ');`
2. Возьми первое число: `int.Parse(parts[0])`.
3. Возьми второе число: `int.Parse(parts[1])`.
4. Выведи их сумму через `Console.WriteLine(...)`.
5. Запусти программу: введи `{{sample}}` и проверь, что программа вывела `{{expected}}`.

Главная идея: `Split(' ')` делит строку на кусочки, а `int.Parse(...)` превращает каждый кусочек в число.
""";
        return WrapKnownCodeTokens(CollapseBlankLines(text.Trim()));
    }

    private static string EnsureGenericTutorialShape(string description, DraftSpec draft)
    {
        if (LooksLikeTutorialStyle(description))
            return WrapKnownCodeTokens(CollapseBlankLines(description));

        var firstSentence = FirstSentence(description);
        var sample = FirstPublicTestInput(draft, "пример");
        var expected = FirstPublicTestOutput(draft, "результат");
        var text = $$"""
Давай сделаем маленький шаг перед следующей задачей.

Следуй шагам:
1. Прочитай условие: {{firstSentence}}
2. Напиши самый простой вариант решения без лишних действий.
3. Запусти программу и проверь её на маленьком примере.
4. Для ввода `{{sample}}` ожидаемый вывод — `{{expected}}`.

Главная идея: это учебная задача на один новый навык, поэтому решение должно быть коротким и понятным.
""";
        return WrapKnownCodeTokens(CollapseBlankLines(text.Trim()));
    }

    private static string FirstSentence(string text)
    {
        var clean = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        if (string.IsNullOrWhiteSpace(clean)) return "нужно выполнить маленькое действие из текущей темы";
        var match = Regex.Match(clean, @"^(.{20,220}?[.!?])\s", RegexOptions.CultureInvariant);
        if (match.Success) return match.Groups[1].Value.Trim();
        return clean.Length <= 220 ? clean : clean[..220].TrimEnd() + "...";
    }

    private static string FirstPublicTestInput(DraftSpec draft, string fallback)
        => draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Input))?.Input.TrimEnd('\r', '\n') ?? fallback;

    private static string FirstPublicTestOutput(DraftSpec draft, string fallback)
        => draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ExpectedOutput))?.ExpectedOutput.TrimEnd('\r', '\n') ?? fallback;

    private static string SanitizeStudentFacingTitle(string? title)
    {
        var clean = (title ?? string.Empty).Trim();
        clean = Regex.Replace(clean, @"^\s*Подготовка\s+к\s+заданию\s+\d+\s*[\.:\-–—]?\s*\d+[\.:\-–—]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        clean = Regex.Replace(clean, @"^\s*Задание\s+\d+(?:\.\d+)?[\.:\-–—]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(clean) ? "AI-черновик задания" : clean;
    }

    private static string SanitizeStudentFacingDescription(string? description)
    {
        var original = (description ?? string.Empty).Replace("\r\n", "\n").Trim();
        var clean = original;
        clean = Regex.Replace(clean, @"^\s*Место\s+в\s+курсе\..*?(?:\n\s*\n|$)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant).TrimStart();
        clean = Regex.Replace(clean, @"^\s*Это\s+подготовительное\s+задание\s+после.*?(?:\n\s*\n|$)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant).TrimStart();
        clean = RemoveInternalRubricSections(clean);
        clean = RemoveInternalRequirementLines(clean);
        clean = CollapseBlankLines(clean).Trim();
        if (string.IsNullOrWhiteSpace(clean))
            clean = FirstStudentFacingParagraph(original);
        clean = WrapKnownCodeTokens(clean);
        return clean;
    }

    private static string RemoveInternalRubricSections(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var kept = new List<string>();
        var skipping = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (IsInternalRubricHeading(line))
            {
                skipping = true;
                continue;
            }

            if (skipping && IsStudentFacingHeading(line))
                skipping = false;

            if (!skipping)
                kept.Add(rawLine);
        }

        return string.Join("\n", kept);
    }

    private static string RemoveInternalRequirementLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var kept = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (LooksLikeInternalRequirementLine(line))
                continue;
            kept.Add(rawLine);
        }

        return string.Join("\n", kept);
    }

    private static bool IsInternalRubricHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return Regex.IsMatch(line, @"^(требования\s+и\s+критерии|критерии\s+при[её]ма|критерии\s+проверки|acceptance\s+criteria|requirements|must\s*not\s*use|ограничения|запрещ[её]нные\s+при[её]мы)\s*[:.]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsStudentFacingHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return Regex.IsMatch(line, @"^(условие|задача|что\s+нужно\s+сделать|подсказка|формат\s+ввода|формат\s+вывода|ввод|вывод|пример|примеры|пояснение)\s*[:.]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeInternalRequirementLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var clean = Regex.Replace(line, @"^[\-•*\d.)\s]+", string.Empty).Trim();
        return Regex.IsMatch(clean, @"^(программа\s+должна\s+использовать|тесты\s+проверяют|нельзя\s+(применять|использовать)|не\s+используйте|запрещается|запрещено|do\s+not\s+use|must\s+not\s+use|use\s+only)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string FirstStudentFacingParagraph(string text)
    {
        foreach (var part in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            var candidate = part.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && !IsInternalRubricHeading(candidate))
                return candidate;
        }
        return text.Trim();
    }

    private static string CollapseBlankLines(string text)
    {
        var normalized = Regex.Replace(text.Replace("\r\n", "\n"), @"[ \t]+\n", "\n", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        return normalized;
    }

    private static string WrapKnownCodeTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var spans = new List<string>();
        string Protect(string value)
        {
            var marker = $"\u0001{spans.Count}\u0002";
            spans.Add(value);
            return marker;
        }

        var result = Regex.Replace(text, @"`[^`]*`", m => Protect(m.Value), RegexOptions.CultureInvariant);

        string WrapPattern(string input, string pattern, Func<Match, string> replacement)
        {
            return Regex.Replace(input, pattern, m => Protect(replacement(m)), RegexOptions.CultureInvariant);
        }

        result = result.Replace("$\"Привет, {name}!\"", Protect("`$\"Привет, {name}!\"`"));
        result = WrapPattern(result, @"\bConsole\.ReadLine\s*\(\s*\)", m => "`Console.ReadLine()`");
        result = WrapPattern(result, @"\bConsole\.WriteLine\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\bConsole\.WriteLine\b", m => "`Console.WriteLine(...)`");
        result = WrapPattern(result, @"\bConsole\.Write\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\bConsole\.Write\b", m => "`Console.Write(...)`");
        result = WrapPattern(result, @"\bint\.Parse\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\bint\.Parse\b", m => "`int.Parse(...)`");
        result = WrapPattern(result, @"\bConvert\.ToInt32\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\bConvert\.ToInt32\b", m => "`Convert.ToInt32(...)`");
        result = WrapPattern(result, @"\bStringSplitOptions\.RemoveEmptyEntries\b", m => "`StringSplitOptions.RemoveEmptyEntries`");
        result = WrapPattern(result, @"\bSplit\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\bSplit\b", m => "`Split(...)`");
        result = WrapPattern(result, @"\bstring\b", m => "`string`");
        result = WrapPattern(result, @"\bint\b", m => "`int`");

        for (var i = 0; i < spans.Count; i++)
            result = result.Replace($"\u0001{i}\u0002", spans[i]);

        return result;
    }


    private static List<string> BuildPublicTags(IEnumerable<string> tags)
    {
        var result = new List<string>();
        foreach (var tag in tags)
        {
            var clean = (tag ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;
            var lower = clean.ToLowerInvariant();
            if (lower == "learning-bridge" || lower.StartsWith("learning-bridge-step-", StringComparison.Ordinal) || lower.StartsWith("skill:", StringComparison.Ordinal) || lower.StartsWith("input-onboarding", StringComparison.Ordinal))
                continue;
            if (!result.Contains(clean, StringComparer.OrdinalIgnoreCase)) result.Add(clean);
        }
        return result;
    }

    private static List<string> MergeTags(List<string> tags, IEnumerable<string> required)
    {
        var result = new List<string>();
        foreach (var tag in tags.Concat(required))
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var clean = tag.Trim();
            if (!result.Contains(clean, StringComparer.OrdinalIgnoreCase)) result.Add(clean);
        }
        return result;
    }

    private static List<DraftSpec> NormalizeDraftOrder(List<DraftSpec> drafts)
    {
        var sourceIndexes = drafts.Where(x => x.SourceTaskIndex.HasValue).Select(x => x.SourceTaskIndex!.Value).ToList();
        var looksOneBased = sourceIndexes.Count > 0 && sourceIndexes.Min() == 1 && !sourceIndexes.Contains(0);
        return drafts
            .Select((draft, index) => new { draft, index, sort = draft.SourceTaskIndex.HasValue ? (looksOneBased ? draft.SourceTaskIndex.Value - 1 : draft.SourceTaskIndex.Value) : index })
            .OrderBy(x => x.sort)
            .ThenBy(x => x.index)
            .Select(x => x.draft)
            .ToList();
    }

    public static bool LooksLikeMultipleDraftRequest(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("задачки")
               || t.Contains("обучалки")
               || t.Contains("несколько")
               || t.Contains("серия")
               || t.Contains("набор")
               || t.Contains("лестниц")
               || t.Contains("guided ladder")
               || t.Contains("learning bridge")
               || t.Contains("bridge task")
               || CourseSkillAnalyzer.LooksLikeLearningBridgeRequest(text);
    }

    private static string? ExtractJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var first = t.IndexOf('\n');
            var last = t.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first) t = t[(first + 1)..last].Trim();
        }

        var balancedObject = ExtractFirstBalanced(t, '{', '}');
        var balancedArray = ExtractFirstBalanced(t, '[', ']');
        if (balancedObject != null && (balancedArray == null || t.IndexOf('{') < t.IndexOf('['))) return balancedObject;
        if (balancedArray != null) return balancedArray;

        var objectStart = t.IndexOf('{');
        var objectEnd = t.LastIndexOf('}');
        var arrayStart = t.IndexOf('[');
        var arrayEnd = t.LastIndexOf(']');
        if (objectStart >= 0 && objectEnd > objectStart && (arrayStart < 0 || objectStart < arrayStart)) return t[objectStart..(objectEnd + 1)];
        if (arrayStart >= 0 && arrayEnd > arrayStart) return t[arrayStart..(arrayEnd + 1)];
        return null;
    }

    private static string? ExtractFirstBalanced(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (inString)
            {
                if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"')
            {
                inString = true;
                continue;
            }
            if (ch == open) depth++;
            else if (ch == close)
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
                if (depth < 0) return null;
            }
        }
        return null;
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node == null) return null;
        return int.TryParse(node.ToString(), out var value) ? value : null;
    }

    private static IEnumerable<string> ReadStringArray(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var value = item?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }
        else if (!string.IsNullOrWhiteSpace(node?.ToString()))
        {
            foreach (var item in node!.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return item;
        }
    }

    private static List<TestCaseSpec> ReadTests(JsonNode? node, bool hidden)
    {
        var result = new List<TestCaseSpec>();
        if (node is not JsonArray arr) return result;
        foreach (var item in arr.OfType<JsonObject>())
        {
            result.Add(new TestCaseSpec
            {
                Input = item["input"]?.ToString() ?? string.Empty,
                ExpectedOutput = item["expectedOutput"]?.ToString() ?? item["output"]?.ToString() ?? string.Empty,
                IsHidden = hidden || bool.TryParse(item["isHidden"]?.ToString(), out var isHidden) && isHidden
            });
        }
        return result;
    }

    private static string NormalizeLanguage(string? value)
    {
        var text = (value ?? "cpp").Trim().ToLowerInvariant();
        return text switch
        {
            "c#" or "csharp" or "cs" or "sharp" or "с#" or "си#" => "csharp",
            "py" or "python" or "python3" => "python",
            "js" or "node" or "nodejs" or "javascript" => "javascript",
            "pas" or "pascal" => "pascal",
            "java" => "java",
            "ru" => "cpp",
            _ => string.IsNullOrWhiteSpace(text) ? "cpp" : text
        };
    }


}
