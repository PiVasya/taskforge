using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Workflows.Executors;

namespace TaskForge.AiAgent.Workflows;

public sealed class PolishAssignmentDraftWorkflow : ITaskForgeWorkflow
{
    private readonly LoadRunContextExecutor _loadContext;
    private readonly DraftValidationExecutor _validator;
    private readonly DraftCriticExecutor _critic;
    private readonly ApprovalGateExecutor _approval;
    private readonly ResultEnvelopeBuilder _envelopes;
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private readonly TaskForgeAgentOptions _options;
    private AIAgent? _agent;

    public PolishAssignmentDraftWorkflow(
        LoadRunContextExecutor loadContext,
        DraftValidationExecutor validator,
        DraftCriticExecutor critic,
        ApprovalGateExecutor approval,
        ResultEnvelopeBuilder envelopes,
        TaskForgeAgentFactory agentFactory,
        AgentSessionStore sessionStore,
        AgentStepReporter steps,
        IOptions<TaskForgeAgentOptions> options)
    {
        _loadContext = loadContext;
        _validator = validator;
        _critic = critic;
        _approval = approval;
        _envelopes = envelopes;
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
        _options = options.Value;
    }

    public string Name => "polish_assignment_draft_workflow";
    public int Priority => 95;

    public bool CanHandle(ClaimedAgentJob job)
    {
        if (string.Equals(job.JobType, "polish_assignment_draft", StringComparison.OrdinalIgnoreCase))
            return true;

        var action = job.Payload.GetPropertyOrDefault("request").GetStringOrNull("action")
            ?? job.Payload.GetStringOrNull("action");
        return string.Equals(action, "polish_assignment_draft", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var state = new WorkflowState { Job = job, WorkflowName = Name, ScenarioId = "polish_assignment_draft" };
        var context = await _loadContext.ExecuteAsync(state);
        var request = job.Payload.GetPropertyOrDefault("request");
        var selectedTask = request.GetPropertyOrDefault("selectedTask");
        var sourceTaskIndex = request.GetIntOrNull("taskIndex") ?? selectedTask.GetIntOrNull("sourceTaskIndex", "index");
        var beforeAssignmentId = request.GetGuidOrNull("beforeAssignmentId");
        var afterAssignmentId = request.GetGuidOrNull("afterAssignmentId");
        var note = request.GetStringOrNull("note") ?? string.Empty;
        var draftConversationContext = request.GetPropertyOrDefault("draftConversationContext");
        var draftConversationContextJson = draftConversationContext.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : draftConversationContext.GetRawText();

        var selectedTaskJson = selectedTask.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : selectedTask.GetRawText();

        DraftSpec? acceptedDraft = null;
        var repairInstruction = string.Empty;

        for (var attempt = 0; attempt <= _options.MaxDraftRepairAttempts; attempt++)
        {
            var draft = await BuildPolishedDraftAsync(
                state,
                context,
                selectedTaskJson,
                note,
                draftConversationContextJson,
                sourceTaskIndex,
                beforeAssignmentId,
                afterAssignmentId,
                repairInstruction,
                attempt,
                cancellationToken);

            var validation = await _validator.ExecuteAsync(state, draft);
            var critique = await _critic.ExecuteAsync(state, validation, cancellationToken);
            if (critique["isAccepted"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            {
                acceptedDraft = draft;
                break;
            }

            repairInstruction = $"Учти замечания критика и верни исправленный JSON: {critique.ToJsonString()}";
            state.Notes.Add($"Polish draft attempt {attempt + 1} rejected by critique.");
        }

        if (acceptedDraft == null)
        {
            state.Artifacts.Add(new AgentArtifact("draft_validation_failed", "Ассистент не смог подготовить валидный скрытый черновик", new JsonObject
            {
                ["reason"] = "Draft was rejected by validation/critic after repair attempts. Hidden draft was not created.",
                ["selectedTask"] = TryParseNode(selectedTaskJson),
                ["lastValidation"] = state.Data.TryGetPropertyValue("draftShapeValidation", out var shape) ? shape?.DeepClone() : null,
                ["lastTestRun"] = state.Data.TryGetPropertyValue("testRun", out var testRun) ? testRun?.DeepClone() : null,
                ["lastCritique"] = state.Data.TryGetPropertyValue("critique", out var critique) ? critique?.DeepClone() : null
            }));
            state.AssistantMessage = "Я попытался доработать задание и прогнать решение, но черновик не прошёл проверки. Скрытый черновик не создан.";
            return _envelopes.FromWorkflowState(state);
        }

        await _approval.ExecuteForPolishedDraftAsync(state, acceptedDraft);
        state.AssistantMessage = "Я доработал выбранное задание, проверил структуру и тесты, затем подготовил материал для скрытого черновика. Черновик остаётся скрытым до ручной публикации.";
        return _envelopes.FromWorkflowState(state);
    }

    private async Task<DraftSpec> BuildPolishedDraftAsync(
        WorkflowState state,
        string context,
        string selectedTaskJson,
        string note,
        string draftConversationContextJson,
        int? sourceTaskIndex,
        Guid? beforeAssignmentId,
        Guid? afterAssignmentId,
        string repairInstruction,
        int attempt,
        CancellationToken cancellationToken)
    {
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        await _steps.TryReportAsync("polish_draft", "running", attempt == 0 ? "Дорабатываю выбранное задание" : $"Исправляю доработанный черновик, попытка {attempt + 1}", note);

        var session = await _sessionStore.LoadAsync(_agent, state.Job.ConversationId, cancellationToken);
        var prompt = $$"""
{{TaskForgeAgentPrompts.DraftAuthor}}

Пользователь выбрал уже сгенерированное задание и просит доработать его до состояния скрытого черновика в TaskForge.
Не создавай новую тему с нуля: сохрани смысл selectedTask, улучши формулировку, тесты, эталонное решение и метаданные.

selectedTask JSON:
{{selectedTaskJson}}

Позиционирование в курсе:
- sourceTaskIndex: {{sourceTaskIndex?.ToString() ?? "null"}}
- beforeAssignmentId: {{beforeAssignmentId?.ToString() ?? "null"}}
- afterAssignmentId: {{afterAssignmentId?.ToString() ?? "null"}}

Заметка пользователя:
{{note}}

Контекст предыдущего чата для стиля и требований draft:
{{draftConversationContextJson}}

Важно: если пользователь несколькими сообщениями ранее описывал стиль, формат, язык, сложность, тип формулировок или ограничения, обязательно перенеси эти требования в итоговый draft.

{{repairInstruction}}

Контекст курса и соседних заданий:
{{context}}

Верни строго JSON без markdown:
{
  "assignmentType": "code-test|test|math",
  "title": "короткое понятное название",
  "description": "полное условие на русском",
  "language": "cpp|csharp|java|javascript|pascal|python",
  "referenceSolution": "код эталонного решения для code-test",
  "difficulty": 1,
  "rating": 10,
  "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
  "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
  "testSpec": {"settings": {}, "questions": [{"type":"single-choice|multi-choice|fill|text", "prompt":"...", "options":[{"key":"a", "text":"..."}], "correctOptionKeys":["a"], "acceptedAnswers":["..."]}]},
  "mathSpec": {"settings": {}, "blocks": [{"kind":"info|number|expression|set|single-choice|multi-choice|order|match", "prompt":"...", "score":1, "acceptedAnswers":["..."], "options":[{"key":"a", "text":"..."}], "correctOptionKeys":["a"]}]},
  "tags": ["AI", "черновик"]
}
""";
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);

        var draft = ParseDraft(response.Text ?? string.Empty, state.Job, selectedTaskJson, sourceTaskIndex, beforeAssignmentId, afterAssignmentId);
        state.Draft = draft;
        await _steps.TryReportAsync("polish_draft", "completed", "Доработанный черновик подготовлен", draft.Title, draft.ToArtifactData());
        return draft;
    }

    private DraftSpec ParseDraft(string text, ClaimedAgentJob job, string selectedTaskJson, int? sourceTaskIndex, Guid? beforeAssignmentId, Guid? afterAssignmentId)
    {
        try
        {
            var json = ExtractJson(text);
            if (json != null && JsonNode.Parse(json) is JsonObject node)
            {
                var draft = new DraftSpec
                {
                    AssignmentType = node["assignmentType"]?.ToString() ?? node["taskType"]?.ToString() ?? "code-test",
                    Title = node["title"]?.ToString() ?? "Доработанное задание",
                    Description = node["description"]?.ToString() ?? node["condition"]?.ToString() ?? "Описание задания не было заполнено моделью.",
                    Language = NormalizeLanguage(node["language"]?.ToString() ?? "cpp"),
                    ReferenceSolution = node["referenceSolution"]?.ToString() ?? node["solution"]?.ToString() ?? string.Empty,
                    Difficulty = int.TryParse(node["difficulty"]?.ToString(), out var d) ? Math.Clamp(d, 1, 3) : 1,
                    Rating = int.TryParse(node["rating"]?.ToString(), out var r) ? Math.Max(1, r) : 10,
                    CourseId = job.CourseId,
                    BeforeAssignmentId = beforeAssignmentId,
                    AfterAssignmentId = afterAssignmentId,
                    SourceTaskIndex = sourceTaskIndex,
                    Tags = ReadStringArray(node["tags"]).DefaultIfEmpty("AI").ToList(),
                    PublicTests = ReadTests(node["publicTests"], false),
                    HiddenTests = ReadTests(node["hiddenTests"], true),
                    TestSpec = ReadTestSpec(node),
                    MathSpec = ReadMathSpec(node),
                    Extra = new JsonObject
                    {
                        ["rawModelDraft"] = text.Length > 6000 ? text[..6000] : text,
                        ["selectedTask"] = TryParseNode(selectedTaskJson)
                    }
                };

                if (draft.PublicTests.Count == 0 && draft.HiddenTests.Count == 0)
                    AddTestsFromSelectedTask(draft, selectedTaskJson);

                if (!draft.Tags.Any(x => x.Equals("полировка", StringComparison.OrdinalIgnoreCase)))
                    draft.Tags.Add("полировка");

                return draft;
            }
        }
        catch
        {
            // fallback below
        }

        return BuildFallbackDraft(job, ParseSelectedTaskElement(selectedTaskJson), sourceTaskIndex, beforeAssignmentId, afterAssignmentId);
    }

    private DraftSpec BuildFallbackDraft(ClaimedAgentJob job, JsonElement selectedTask, int? sourceTaskIndex, Guid? beforeAssignmentId, Guid? afterAssignmentId)
    {
        var draft = new DraftSpec
        {
            AssignmentType = selectedTask.GetStringOrNull("assignmentType", "taskType", "type") ?? "code-test",
            Title = selectedTask.GetStringOrNull("title", "name") ?? "Доработанное задание",
            Description = selectedTask.GetStringOrNull("description", "condition", "body") ?? $"Доработать выбранное задание по запросу: {job.UserText}",
            Language = NormalizeLanguage(selectedTask.GetStringOrNull("language", "lang") ?? "cpp"),
            ReferenceSolution = selectedTask.GetStringOrNull("referenceSolution", "solution", "answer") ?? string.Empty,
            Difficulty = Math.Clamp(selectedTask.GetIntOrNull("difficulty") ?? 1, 1, 3),
            Rating = Math.Max(1, selectedTask.GetIntOrNull("rating", "points", "score") ?? 10),
            CourseId = job.CourseId,
            BeforeAssignmentId = beforeAssignmentId,
            AfterAssignmentId = afterAssignmentId,
            SourceTaskIndex = sourceTaskIndex,
            Tags = new List<string> { "черновик", "доработка", "needs-review" },
            Extra = new JsonObject { ["parseFallback"] = true, ["selectedTask"] = TryParseNode(GetRawTextOrEmptyObject(selectedTask)) }
        };

        AddTestsFromSelectedTask(draft, GetRawTextOrEmptyObject(selectedTask));
        return draft;
    }

    private static string GetRawTextOrEmptyObject(JsonElement element)
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : element.GetRawText();

    private static JsonElement ParseSelectedTaskElement(string selectedTaskJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(selectedTaskJson) ? "{}" : selectedTaskJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
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

    private static void AddTestsFromSelectedTask(DraftSpec draft, string selectedTaskJson)
    {
        var selected = TryParseNode(selectedTaskJson) as JsonObject;
        if (selected == null) return;

        draft.PublicTests.AddRange(ReadTests(selected["publicTests"], false));
        draft.HiddenTests.AddRange(ReadTests(selected["hiddenTests"], true));
        draft.TestSpec ??= ReadTestSpec(selected);
        draft.MathSpec ??= ReadMathSpec(selected);

        foreach (var test in ReadTests(selected["testCases"] ?? selected["tests"], false))
        {
            if (test.IsHidden) draft.HiddenTests.Add(test);
            else draft.PublicTests.Add(test);
        }
    }

    private static JsonNode? TryParseNode(string raw)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
        catch { return new JsonObject { ["raw"] = raw.Length > 6000 ? raw[..6000] : raw }; }
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
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : null;
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

    private static string NormalizeLanguage(string? language)
    {
        var value = (language ?? "cpp").Trim().ToLowerInvariant();
        return value switch
        {
            "c++" or "cpp" => "cpp",
            "c#" or "cs" or "csharp" => "csharp",
            "js" or "javascript" => "javascript",
            "py" or "python" => "python",
            "java" => "java",
            "pascal" => "pascal",
            "ru" => "cpp",
            _ => string.IsNullOrWhiteSpace(value) ? "cpp" : value
        };
    }
}
