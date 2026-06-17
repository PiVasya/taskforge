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

    private static string ExtractPayloadProperty(JsonElement payload, string name)
    {
        try
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value))
            {
                var raw = value.GetRawText();
                return raw.Length <= 30000 ? raw : raw[..30000] + "...";
            }
        }
        catch
        {
            // Agent-loop memory is an optional quality context. Broken payload must not break draft generation.
        }

        return "{}";
    }

    private int ResolveDraftCount(int requestedCount, CourseSkillBridgeContext bridge)
    {
        var maxDrafts = Math.Clamp(_options.MaxDraftsPerRun, 1, 50);
        var requested = Math.Clamp(requestedCount, 1, maxDrafts);
        var planned = bridge.BridgePlan?.Count ?? 0;
        if (planned > 0) return Math.Clamp(planned, 1, Math.Min(maxDrafts, requested));
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

    private static string BuildCompactDraftPrompt(WorkflowState state, string bridgeJson, string teacherPreferences, string agentLoopMemory, int count)
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

Память adaptive agent loop:
{{agentLoopMemory}}

Требования:
- Один draft на один bridgePlan step, в том же порядке.
- Один главный новый навык на draft.
- Не использовать mustNotUse текущего step.
- Student-facing title/description, без служебной metadata.
- Description не должен содержать внутренние требования валидатора, acceptanceCriteria, mustNotUse, списки запретов и фразы вроде "Требования и критерии приёма", "Программа должна использовать", "Нельзя применять".
- Пиши learning-bridge как маленький туториал: дружелюбная вводная, "Следуй шагам:", 3-5 нумерованных действий, пояснение каждой важной строки и финал "Запусти код и проверь...". Не пиши сухое "Считать X и вывести Y" без обучения.
- Не добавляй сухие секции "Формат ввода", "Формат вывода", "Ввод", "Вывод", "Критерии". Формат ввода объясни одной простой фразой внутри урока.
- Не пиши "самый короткий" и не поощряй code golf; проси понятное минимально необходимое решение.
- Делай обучалку максимально простой: если можно одной строкой кода — используй одну строку. Не добавляй проверки ошибок, условия, префиксы вывода или будущие темы без необходимости текущего step.
- Если это code-test, дай referenceSolution, 2 publicTests и 2 hiddenTests, которые проходят решение.
- Если это test, дай testSpec.questions с валидными вопросами, вариантами/ответами и понятными формулировками для ученика.
- Если это math, дай mathSpec.blocks с валидными блоками, ответами и пояснениями.
- Если для какого-то step невозможно дать корректный draft с данными проверки, просто пропусти этот step, не выдумывай fallback.
- language выбирай из языка соседних/целевых заданий или allowedLanguages из COURSE_SKILL_MAP. Не подставляй конкретный язык, если он не следует из курса.

Верни только валидный JSON без markdown:
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
            Title = node["title"]?.ToString() ?? "Черновик задания",
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
            TestSpec = ReadTestSpec(node),
            MathSpec = ReadMathSpec(node),
            Extra = extra
        };
    }

    private static JsonObject? ReadTestSpec(JsonObject node)
    {
        if (node["testSpec"] is JsonObject spec)
            return spec.DeepClone() as JsonObject;
        if (node["test"] is JsonObject test)
            return test.DeepClone() as JsonObject;
        if (node["taskTest"] is JsonObject taskTest)
            return taskTest.DeepClone() as JsonObject;
        if (node["questions"] is JsonArray questions)
        {
            return new JsonObject
            {
                ["settings"] = new JsonObject(),
                ["questions"] = questions.DeepClone()
            };
        }
        return null;
    }

    private static JsonObject? ReadMathSpec(JsonObject node)
    {
        if (node["mathSpec"] is JsonObject spec)
            return spec.DeepClone() as JsonObject;
        if (node["math"] is JsonObject math)
            return math.DeepClone() as JsonObject;
        if (node["taskMath"] is JsonObject taskMath)
            return taskMath.DeepClone() as JsonObject;
        if (node["blocks"] is JsonArray blocks)
        {
            return new JsonObject
            {
                ["settings"] = new JsonObject(),
                ["blocks"] = blocks.DeepClone()
            };
        }
        return null;
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

        // Keep raw LLM text out of draft metadata. It is noisy, can leak internal
        // prompt/repair text into служебные материалы, and may confuse the model critic
        // into critiquing stale rawModelDraft content instead of the normalized
        // student-facing assignment.
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

        // Do not replace model output with subject/language-specific canned code.
        // The author LLM must generate the actual solution from COURSE_SKILL_MAP;
        // this normalizer only keeps the visible text in tutorial form.
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

            if (draft.Extra["bridgeSkillId"] is not null && draft.Extra["modelBridgeSkillId"] is null)
                draft.Extra["modelBridgeSkillId"] = draft.Extra["bridgeSkillId"]!.DeepClone();
            if (draft.Extra["introducedSkillIds"] is not null && draft.Extra["modelIntroducedSkillIds"] is null)
                draft.Extra["modelIntroducedSkillIds"] = draft.Extra["introducedSkillIds"]!.DeepClone();

            var skillId = plannedStep["skillId"]?.ToString();
            if (string.IsNullOrWhiteSpace(skillId))
                skillId = CourseSkillAnalyzer.NormalizeSkillId(string.Join(" ", ReadStringArray(plannedStep["introducedSkills"])));
            if (!string.IsNullOrWhiteSpace(skillId))
            {
                draft.Extra["plannedBridgeSkillId"] = skillId;
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


    internal static string EnsureLearningBridgeTutorialStyle(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var clean = (draft.Description ?? string.Empty).Trim();
        if (!LooksLikeLearningBridgeDraft(draft, bridge))
            return WrapKnownCodeTokens(CollapseBlankLines(clean));

        clean = StripDryTaskSections(clean);
        if (LooksLikeTutorialStyle(clean) && !NeedsTutorialRewrite(clean))
            return WrapKnownCodeTokens(CollapseBlankLines(clean));

        return EnsureGenericTutorialShape(clean, draft, bridge, stepIndex);
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
        var hasTeachingTone = lower.Contains("давай") || lower.Contains("научимся") || lower.Contains("научись") || lower.Contains("запусти") || lower.Contains("проверь") || lower.Contains("попробуй");
        return hasSteps && hasTeachingTone;
    }

    private static bool NeedsTutorialRewrite(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("самый короткий") || lower.Contains("короткий короткий") || lower.Contains("code golf") || lower.Contains("гольф"))
            return true;
        if (!lower.Contains("запусти") && !lower.Contains("проверь"))
            return true;
        if (HasDryTaskHeading(text)) return true;
        if (Regex.IsMatch(text, @"для\s+ввода\s+`[^`]*\n[^`]*`", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return false;
    }

    private static string EnsureGenericTutorialShape(string description, DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var stepHint = DescribeBridgeStep(bridge, stepIndex, description);
        var inputNote = BuildFriendlyInputNote(draft);
        var steps = BuildTutorialStepsFromReferenceSolution(draft.ReferenceSolution, draft.Language).ToList();
        if (steps.Count == 0)
        {
            steps.Add($"1. Разбери новый приём: {WrapInline(stepHint)}.");
            steps.Add("(Это маленький шаг, который понадобится в следующей задаче.)");
            steps.Add("2. Напиши понятное минимально необходимое решение.");
            steps.Add("(Не добавляй лишние проверки и темы, которые здесь ещё не нужны.)");
        }

        var check = BuildFriendlyCheckSentence(draft);
        var intro = BuildFriendlyIntro(bridge, stepIndex, stepHint);
        var noteBlock = string.IsNullOrWhiteSpace(inputNote) ? string.Empty : inputNote + "\n\n";
        var text = $$"""
{{intro}}

{{noteBlock}}Следуй шагам:
{{string.Join("\n", steps)}}

{{check}}
""";
        return WrapKnownCodeTokens(CollapseBlankLines(text.Trim()));
    }

    private static string BuildFriendlyIntro(CourseSkillBridgeContext bridge, int stepIndex, string stepHint)
    {
        var normalized = NormalizeForTutorialText(stepHint);
        if (normalized.Contains("readline") || normalized.Contains("ввод") || normalized.Contains("считать") || normalized.Contains("прочит"))
            return "Давай научимся получать данные из консоли маленькими шагами.";
        if (normalized.Contains("parse") || normalized.Contains("числ") || normalized.Contains("преобраз"))
            return "Давай научимся превращать введённый текст в число.";
        return "Давай сделаем маленький учебный шаг перед следующей задачей.";
    }

    private static string BuildFriendlyInputNote(DraftSpec draft)
    {
        var input = draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Input))?.Input;
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var lines = SplitInputLines(input).ToList();
        if (lines.Count > 1)
            return lines.Count == 2
                ? "Будем считать, что пользователь вводит два корректных значения: первое с новой строки и второе с новой строки."
                : $"Будем считать, что пользователь вводит {lines.Count} корректных значения, каждое с новой строки.";
        return "Будем считать, что пользователь вводит корректное значение.";
    }

    private static string BuildFriendlyCheckSentence(DraftSpec draft)
    {
        var sampleInput = draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Input))?.Input;
        var expected = draft.PublicTests.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ExpectedOutput))?.ExpectedOutput;
        if (string.IsNullOrWhiteSpace(sampleInput) || string.IsNullOrWhiteSpace(expected))
            return "Запусти код и проверь, что программа делает именно этот маленький шаг.";

        var inputText = DescribeInputForStudent(sampleInput);
        var outputText = ToInlineCode(CleanOneLineValue(expected));
        return $"Запусти код и проверь: если ввести {inputText}, на экране появится {outputText}.";
    }

    private static IEnumerable<string> BuildTutorialStepsFromReferenceSolution(string? solution, string? language)
    {
        var groups = BuildCodeGroups(solution).ToList();
        if (groups.Count == 0) yield break;

        if (groups.Count > 5)
            groups = CompactCodeGroups(groups);
        if (groups.Count > 5)
            groups = groups.Take(5).ToList();

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            yield return $"{i + 1}. {BuildStepAction(group)}";
            yield return $"({BuildStepExplanation(group)})";
        }
    }

    private sealed record CodeGroup(string Kind, List<string> Lines);

    private static IEnumerable<CodeGroup> BuildCodeGroups(string? solution)
    {
        if (string.IsNullOrWhiteSpace(solution)) yield break;
        var groups = new List<CodeGroup>();
        foreach (var raw in solution.Replace("\r\n", "\n").Split('\n'))
        {
            var line = NormalizeCodeLine(raw);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (IsCommentOnlyCodeLine(line)) continue;
            if (line is "{" or "}" or "};") continue;

            var kind = ClassifyCodeLine(line);
            var last = groups.LastOrDefault();
            if (last is not null && last.Kind == kind && CanGroupCodeLines(kind))
            {
                last.Lines.Add(line);
                continue;
            }

            groups.Add(new CodeGroup(kind, new List<string> { line }));
        }

        foreach (var group in groups)
            yield return group;
    }

    private static List<CodeGroup> CompactCodeGroups(List<CodeGroup> groups)
    {
        var result = new List<CodeGroup>();
        foreach (var group in groups)
        {
            var last = result.LastOrDefault();
            if (last is not null && (last.Kind == group.Kind || group.Kind == "start" && last.Kind == "start"))
            {
                last.Lines.AddRange(group.Lines);
                continue;
            }
            result.Add(new CodeGroup(group.Kind, group.Lines.ToList()));
        }

        if (result.Count <= 5) return result;

        var import = result.FirstOrDefault(x => x.Kind == "import");
        var start = result.FirstOrDefault(x => x.Kind == "start");
        if (import is not null && start is not null)
        {
            start.Lines.InsertRange(0, import.Lines);
            result.Remove(import);
        }

        return result;
    }

    private static string BuildStepAction(CodeGroup group)
    {
        var code = JoinCodeChips(group.Lines);
        return group.Kind switch
        {
            "import" => $"Подключи нужную библиотеку: {code}",
            "start" => $"Напиши начало программы: {code}",
            "input" => $"Считай данные из консоли: {code}",
            "parse" => $"Преобразуй введённый текст в число: {code}",
            "compute" => $"Выполни вычисление: {code}",
            "output" => $"Выведи результат: {code}",
            _ => $"Напиши строку: {code}"
        };
    }

    private static string BuildStepExplanation(CodeGroup group)
    {
        return group.Kind switch
        {
            "import" => "Эта строка подключает команды, которые нужны программе.",
            "start" => "Так начинается основная часть программы.",
            "input" => group.Lines.Count > 1 ? "Эти строки получают значения, которые пользователь вводит с клавиатуры." : "Эта строка получает значение, которое пользователь вводит с клавиатуры.",
            "parse" => group.Lines.Count > 1 ? "Эти строки превращают введённый текст в числа." : "Эта строка превращает введённый текст в число.",
            "compute" => "Здесь выполняется простое вычисление.",
            "output" => "Эта строка показывает результат на экране.",
            _ => "Эта строка нужна для текущего маленького шага."
        };
    }

    private static string NormalizeCodeLine(string raw)
    {
        var line = raw.Trim();
        line = Regex.Replace(line, @"\s+", " ", RegexOptions.CultureInvariant);
        return line;
    }

    private static bool IsCommentOnlyCodeLine(string line)
        => line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal);

    private static string ClassifyCodeLine(string line)
    {
        var normalized = NormalizeForTutorialText(line);
        if (normalized.Contains("#include") || normalized.StartsWith("using ") || normalized.StartsWith("import ") || normalized.StartsWith("from "))
            return "import";
        if (normalized.Contains("main") || normalized.Contains("class program") || normalized.Contains("namespace") || normalized.Contains("public class"))
            return "start";
        if (normalized.Contains("readline") || normalized.Contains("readln") || normalized.Contains("scanf") || normalized.Contains("cin") || normalized.Contains("input("))
            return "input";
        if (normalized.Contains("parse") || normalized.Contains("toint") || normalized.Contains("convert.") || normalized.Contains("int(input") || normalized.Contains("stoi") || normalized.Contains("strconv"))
            return "parse";
        if (normalized.Contains("writeline") || normalized.Contains("write(") || normalized.Contains("cout") || normalized.Contains("printf") || normalized.Contains("println") || normalized.StartsWith("print"))
            return "output";
        if (Regex.IsMatch(line, @"=.+[+\-*/%]", RegexOptions.CultureInvariant))
            return "compute";
        return "other";
    }

    private static bool CanGroupCodeLines(string kind)
        => kind is "import" or "start" or "input" or "parse" or "output" or "compute";

    private static string JoinCodeChips(IEnumerable<string> lines)
    {
        var chips = lines.Select(ToInlineCode).ToList();
        if (chips.Count == 0) return "`...`";
        if (chips.Count == 1) return chips[0];
        if (chips.Count == 2) return chips[0] + " и " + chips[1];
        return string.Join(", ", chips.Take(chips.Count - 1)) + " и " + chips[^1];
    }

    private static string DescribeInputForStudent(string input)
    {
        var lines = SplitInputLines(input).ToList();
        if (lines.Count > 1 && lines.Count <= 4)
            return string.Join(" и ", lines.Select(ToInlineCode)) + " с новой строки";
        return ToInlineCode(CleanOneLineValue(input));
    }

    private static IEnumerable<string> SplitInputLines(string input)
    {
        foreach (var line in input.Replace("\r\n", "\n").Trim('\r', '\n').Split('\n'))
        {
            var clean = line.Trim();
            if (!string.IsNullOrEmpty(clean)) yield return clean;
        }
    }

    private static string CleanOneLineValue(string value)
        => value.Replace("\r\n", "\n").Replace("\n", "\\n").Trim();

    private static string ToInlineCode(string value)
        => "`" + EscapeBackticks(value) + "`";

    private static string WrapInline(string value)
        => value.Contains('`') ? value : ToInlineCode(value);

    private static string EscapeBackticks(string value)
        => (value ?? string.Empty).Replace("`", "' ");

    private static string NormalizeForTutorialText(string? value)
        => Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static bool HasDryTaskHeading(string text)
        => Regex.IsMatch(text ?? string.Empty, @"(^|\n)\s*(формат\s+ввода|формат\s+вывода|пример|ввод|вывод|критерии|тесты\s+проверяют)\s*:?\s*(\n|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string StripDryTaskSections(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var skipping = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"^(формат\s+ввода|формат\s+вывода|пример|ввод|вывод|критерии|тесты\s+проверяют)\s*:?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                skipping = true;
                continue;
            }

            if (skipping && (Regex.IsMatch(line, @"^\d+\.\s+", RegexOptions.CultureInvariant) || line.Contains("следуй шагам", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(line)))
            {
                if (!string.IsNullOrWhiteSpace(line)) skipping = false;
            }

            if (!skipping)
                kept.Add(raw);
        }

        var clean = string.Join("\n", kept).Trim();
        return string.IsNullOrWhiteSpace(clean) ? text : clean;
    }

    private static string DescribeBridgeStep(CourseSkillBridgeContext bridge, int stepIndex, string fallbackDescription)
    {
        var step = bridge.BridgePlan != null && stepIndex >= 0 && stepIndex < bridge.BridgePlan.Count
            ? bridge.BridgePlan[stepIndex]
            : null;
        var candidates = new List<string>();
        if (step is not null)
        {
            candidates.Add(step["titleHint"]?.ToString() ?? string.Empty);
            candidates.AddRange(ReadStringArray(step["introducedSkills"]));
            candidates.Add(step["reason"]?.ToString() ?? string.Empty);
        }
        candidates.Add(FirstSentence(fallbackDescription));

        return candidates
            .Select(x => Regex.Replace(x ?? string.Empty, @"\s+", " ").Trim().TrimEnd('.', ':', ';'))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "нужно выполнить маленькое действие из текущей темы";
    }

    private static string FirstSentence(string text)
    {
        var clean = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        if (string.IsNullOrWhiteSpace(clean)) return "нужно выполнить маленькое действие из текущей темы";
        var match = Regex.Match(clean, @"^(.{20,220}?[.!?])\s", RegexOptions.CultureInvariant);
        if (match.Success) return match.Groups[1].Value.Trim();
        return clean.Length <= 220 ? clean : clean[..220].TrimEnd() + "...";
    }

    private static string SanitizeStudentFacingTitle(string? title)
    {
        var clean = (title ?? string.Empty).Trim();
        clean = Regex.Replace(clean, @"^\s*Подготовка\s+к\s+заданию\s+\d+\s*[\.:\-–—]?\s*\d+[\.:\-–—]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        clean = Regex.Replace(clean, @"^\s*Задание\s+\d+(?:\.\d+)?[\.:\-–—]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Черновик задания" : clean;
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

        // Formatting helper only: it does not generate or replace solutions. Keep it
        // broad enough for multiple programming languages so student-facing text gets
        // readable inline-code styling without forcing a C#-specific template.
        result = WrapPattern(result, @"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b[A-Za-z_][A-Za-z0-9_]*\s*\([^\n`]*?\)", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b(?:std::)?(?:cin|cout|printf|scanf|println|print|input|read|readln|writeln)\b", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b(?:string|int|long|double|float|bool|char|var|auto|String|Scanner)\b", m => "`" + m.Value + "`");
        result = WrapPattern(result, @"\b[A-Za-z_][A-Za-z0-9_]*\[[^\n`]*?\]", m => "`" + m.Value + "`");

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


    private static List<DraftSpec> AlignDraftsToBridgePlan(List<DraftSpec> drafts, CourseSkillBridgeContext bridge, int count)
    {
        var planCount = bridge.BridgePlan?.Count ?? 0;
        if (planCount == 0)
            return drafts.Take(count).ToList();

        var targetCount = Math.Min(count, planCount);
        var remaining = drafts.ToList();
        var aligned = new List<DraftSpec>();
        for (var stepIndex = 0; stepIndex < targetCount && remaining.Count > 0; stepIndex++)
        {
            var step = bridge.BridgePlan![stepIndex];
            var bestIndex = -1;
            var bestScore = int.MinValue;
            for (var i = 0; i < remaining.Count; i++)
            {
                var score = ScoreDraftForBridgeStep(remaining[i], step, stepIndex, planCount);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                break;

            // If there is no semantic match, still pass the draft through the validator
            // under the planned step. The critic can then reject it or repair can target
            // the correct step. What we must not do is silently treat its own model skill
            // as the planned skill; AddBridgeExtra keeps modelBridgeSkillId for this reason.
            aligned.Add(remaining[bestIndex]);
            remaining.RemoveAt(bestIndex);
        }

        return aligned;
    }

    private static int ScoreDraftForBridgeStep(DraftSpec draft, JsonObject plannedStep, int stepIndex, int planCount)
    {
        var plannedIds = BuildSkillIdSet(
            ReadStringArray(plannedStep["skillId"])
                .Concat(ReadStringArray(plannedStep["introducedSkillIds"]))
                .Concat(ReadStringArray(plannedStep["introducedSkills"]))
                .Concat(ReadStringArray(plannedStep["titleHint"])));
        var draftIds = BuildSkillIdSet(
            ReadStringArray(draft.Extra["bridgeSkillId"])
                .Concat(ReadStringArray(draft.Extra["introducedSkillIds"]))
                .Concat(ReadStringArray(draft.Extra["introducedSkills"]))
                .Concat(new[] { draft.Title ?? string.Empty })
                .Concat(new[] { draft.Description ?? string.Empty }));

        var score = 0;
        if (plannedIds.Count > 0 && draftIds.Overlaps(plannedIds)) score += 20;
        if (draft.SourceTaskIndex.HasValue)
        {
            var normalized = NormalizeBridgeStepIndex(draft.SourceTaskIndex.Value, planCount);
            if (normalized == stepIndex) score += 4;
        }
        if (draftIds.Count == 0) score += 1;
        return score;
    }

    private static HashSet<string> BuildSkillIdSet(IEnumerable<string> values)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var detected = CourseSkillAnalyzer.DetectCanonicalSkillIds(value).ToList();
            if (detected.Count > 0)
            {
                foreach (var id in detected)
                    AddCanonicalSkillId(result, id);
                continue;
            }

            var direct = CourseSkillAnalyzer.NormalizeSkillId(value);
            AddCanonicalSkillId(result, direct);
        }
        return result;
    }

    private static void AddCanonicalSkillId(HashSet<string> result, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var normalized = id.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "console-input":
            case "console input":
            case "console-readline":
            case "console-readline-echo":
                result.Add("console-input-line");
                return;
            case "parse-int-from-readline":
                result.Add("console-input-line");
                result.Add("parse-int");
                return;
            case "sum-two-ints-from-input":
                result.Add("console-input-line");
                result.Add("parse-int");
                result.Add("multi-line-input");
                result.Add("arithmetic");
                return;
            case "strings":
                result.Add("string-literals");
                return;
            default:
                result.Add(normalized);
                return;
        }
    }

    private static int NormalizeBridgeStepIndex(int stepIndex, int planCount)
    {
        if (stepIndex >= 0 && stepIndex < planCount) return stepIndex;
        if (stepIndex > 0 && stepIndex <= planCount) return stepIndex - 1;
        return stepIndex;
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
            
            "js" or "node" or "nodejs" or "javascript" => "javascript",
            "pas" or "pascal" => "pascal",
            "py" or "python" => "python",
            "java" => "java",
            "ru" => "cpp",
            _ => string.IsNullOrWhiteSpace(text) ? "cpp" : text
        };
    }


}
