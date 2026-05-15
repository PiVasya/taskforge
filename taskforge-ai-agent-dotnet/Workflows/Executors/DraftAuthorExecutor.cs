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
    private AIAgent? _agent;

    public DraftAuthorExecutor(TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps, IOptions<TaskForgeAgentOptions> options)
    {
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
        _options = options.Value;
    }

    public async Task<DraftSpec> ExecuteAsync(WorkflowState state, string contextPrompt, string plan, int attempt, CancellationToken cancellationToken)
    {
        var drafts = await ExecuteManyAsync(state, contextPrompt, plan, attempt, 1, cancellationToken);
        return drafts.FirstOrDefault() ?? BuildFallbackDraft(state.Job, attempt, string.Empty);
    }

    public async Task<List<DraftSpec>> ExecuteManyAsync(WorkflowState state, string contextPrompt, string plan, int attempt, int requestedCount, CancellationToken cancellationToken)
    {
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        await _steps.TryReportAsync("draft", "running", attempt == 0 ? "Генерирую черновики заданий" : $"Перегенерирую черновики, попытка {attempt + 1}", plan);

        var count = Math.Clamp(requestedCount, 1, 6);
        var bridge = CourseSkillAnalyzer.Analyze(state.Job.Payload, state.UserText);
        var beforeAssignmentId = bridge.BeforeAssignmentId;
        var bridgeJson = bridge.ToJsonObject().ToJsonString();
        var prompt = $$"""
{{TaskForgeAgentPrompts.DraftAuthor}}

Пользователь просит: {{state.UserText}}
План: {{plan}}

Сгенерируй {{count}} TaskForge draft-ов как JSON.
Если пользователь просит "задачки", "обучалки", "серия", "несколько" — верни массив drafts по возрастанию сложности.
Пиши title и description на языке пользователя/курса. Если пользователь пишет по-русски, title и description тоже должны быть по-русски.

Педагогическая модель вставки — skill bridge:
{{bridgeJson}}

Правила skill bridge:
1. Не используй заранее зашитую лестницу под конкретную тему. Сначала смотри на acquiredSkillsBeforeAnchor, targetSkillsAtAnchor и missingBridgeSkills.
2. Каждое новое задание должно добавлять один маленький новый навык. Не используй навык, которого нет в acquiredSkillsBeforeAnchor или в introducedSkills текущего/предыдущих bridge-заданий.
3. Если для следующего задания нужен навык, который студент ещё не видел, сначала сделай микрозадание именно на этот навык.
4. Первое bridge-задание должно быть проще целевого anchor-задания и не должно требовать больше одного нового умения.
5. Названия должны быть короткими student-facing названиями, без служебных префиксов вроде "Подготовка к заданию 5" и без повторения номера задания.
6. Связь с местом в курсе держи в extra/metadata, но НЕ пиши в description фразы вроде "Место в курсе", "перед Задание 5", "после List<T>".
7. Все code-token'ы в description оформляй inline-code через одиночные backticks, чтобы редактор показал фон.
8. Для code-test обязательно нужны referenceSolution, минимум 2 publicTests и минимум 2 hiddenTests. Тесты должны соответствовать только тем умениям, которые уже разрешены этим шагом.

Контекст:
{{contextPrompt}}

Верни строго JSON без markdown:
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
      "tags": ["AI", "черновик", "learning-bridge"],
      "extra": {
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

        string responseText;
        try
        {
            var session = await _sessionStore.LoadAsync(_agent, state.Job.ConversationId, cancellationToken);
            var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);
            responseText = response.Text ?? string.Empty;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.Notes.Add($"Draft author LLM call failed on attempt {attempt + 1}: {ex.GetType().Name}: {ex.Message}");
            await _steps.TryReportAsync("draft", "failed", "Не удалось получить ответ LLM для черновиков", ex.Message, new JsonObject
            {
                ["exceptionType"] = ex.GetType().Name,
                ["message"] = ex.Message,
                ["attempt"] = attempt + 1
            });
            var fallback = new List<DraftSpec> { BuildFallbackDraft(state.Job, attempt, string.Empty) };
            ApplyBatchMetadata(fallback, state.Job, bridge);
            state.Draft = fallback.FirstOrDefault();
            return fallback;
        }

        var drafts = ParseDrafts(responseText, state.Job);
        if (drafts.Count == 0)
            drafts.Add(BuildFallbackDraft(state.Job, attempt, responseText));

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
            drafts[i].CourseId = job.CourseId;
            drafts[i].BeforeAssignmentId ??= bridge.BeforeAssignmentId;
            drafts[i].SourceTaskIndex ??= i;

            var requiredTags = new List<string> { "AI", "черновик" };
            if (bridge.IsBridgeRequest || drafts.Count > 1)
            {
                requiredTags.Add("learning-bridge");
                requiredTags.Add($"learning-bridge-step-{i + 1}");
            }
            foreach (var skill in ReadStringArray(drafts[i].Extra["introducedSkills"]).Concat(bridge.MissingBridgeSkills).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
                requiredTags.Add($"skill:{skill}");

            drafts[i].Tags = MergeTags(drafts[i].Tags, requiredTags);
            drafts[i].Language = NormalizeLanguage(drafts[i].Language);
            drafts[i].Description = SanitizeStudentFacingDescription(drafts[i].Description);
            drafts[i].Title = SanitizeStudentFacingTitle(drafts[i].Title);
            drafts[i].Difficulty = Math.Clamp(drafts[i].Difficulty, 1, 3);
            drafts[i].Rating = Math.Max(1, drafts[i].Rating);
            AddBridgeExtra(drafts[i], bridge, i);
        }
    }

    private static void AddBridgeExtra(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        draft.Extra["bridgeStepIndex"] ??= stepIndex;
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
        var clean = (description ?? string.Empty).Replace("\r\n", "\n").Trim();
        clean = Regex.Replace(clean, @"^\s*Место\s+в\s+курсе\..*?(?:\n\s*\n|$)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant).TrimStart();
        clean = Regex.Replace(clean, @"^\s*Это\s+подготовительное\s+задание\s+после.*?(?:\n\s*\n|$)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant).TrimStart();
        clean = WrapKnownCodeTokens(clean);
        return clean;
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
        return drafts
            .Select((draft, index) => new { draft, index })
            .OrderBy(x => x.draft.SourceTaskIndex ?? x.index)
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
        var objectStart = t.IndexOf('{');
        var objectEnd = t.LastIndexOf('}');
        var arrayStart = t.IndexOf('[');
        var arrayEnd = t.LastIndexOf(']');
        if (objectStart >= 0 && objectEnd > objectStart && (arrayStart < 0 || objectStart < arrayStart)) return t[objectStart..(objectEnd + 1)];
        if (arrayStart >= 0 && arrayEnd > arrayStart) return t[arrayStart..(arrayEnd + 1)];
        return null;
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
