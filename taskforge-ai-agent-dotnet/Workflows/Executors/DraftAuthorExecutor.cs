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
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        await _steps.TryReportAsync("draft", "running", attempt == 0 ? "Генерирую черновик задания" : $"Перегенерирую черновик, попытка {attempt + 1}", plan);

        var session = await _sessionStore.LoadAsync(_agent, state.Job.ConversationId, cancellationToken);
        var prompt = $$"""
{{TaskForgeAgentPrompts.DraftAuthor}}

Сгенерируй один качественный TaskForge draft как JSON.
Пользователь просит: {{state.UserText}}
План: {{plan}}

Контекст:
{{contextPrompt}}

Верни строго JSON без markdown:
{
  "assignmentType": "code-test|test|math",
  "title": "...",
  "description": "...",
  "language": "cpp|csharp|java|python|javascript|pascal",
  "referenceSolution": "...",
  "difficulty": 1,
  "rating": 10,
  "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
  "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
  "tags": ["AI", "черновик"]
}
""";
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);

        var draft = ParseDraft(response.Text ?? string.Empty, state.Job, attempt);
        state.Draft = draft;
        await _steps.TryReportAsync("draft", "completed", "Черновик задания подготовлен", draft.Title, draft.ToArtifactData());
        return draft;
    }

    private DraftSpec ParseDraft(string text, ClaimedAgentJob job, int attempt)
    {
        try
        {
            var json = ExtractJson(text);
            if (json != null && JsonNode.Parse(json) is JsonObject node)
            {
                return new DraftSpec
                {
                    AssignmentType = node["assignmentType"]?.ToString() ?? "code-test",
                    Title = node["title"]?.ToString() ?? "AI-задание",
                    Description = node["description"]?.ToString() ?? "Описание задания не было заполнено моделью.",
                    Language = node["language"]?.ToString() ?? "cpp",
                    ReferenceSolution = node["referenceSolution"]?.ToString() ?? string.Empty,
                    Difficulty = int.TryParse(node["difficulty"]?.ToString(), out var d) ? Math.Clamp(d, 1, 3) : 1,
                    Rating = int.TryParse(node["rating"]?.ToString(), out var r) ? Math.Max(1, r) : 10,
                    CourseId = job.CourseId,
                    SourceTaskIndex = attempt,
                    Tags = ReadStringArray(node["tags"]).DefaultIfEmpty("AI").ToList(),
                    PublicTests = ReadTests(node["publicTests"], false),
                    HiddenTests = ReadTests(node["hiddenTests"], true),
                    Extra = new JsonObject { ["rawModelDraft"] = text.Length > 6000 ? text[..6000] : text }
                };
            }
        }
        catch
        {
            // fallback below
        }

        return new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "AI-черновик задания",
            Description = $"Подготовить задание по запросу: {job.UserText}",
            Language = _options.DefaultLanguage == "ru" ? "cpp" : _options.DefaultLanguage,
            Difficulty = 1,
            Rating = 10,
            CourseId = job.CourseId,
            SourceTaskIndex = attempt,
            Tags = new List<string> { "AI", "черновик", "needs-review" },
            Extra = new JsonObject { ["parseFallback"] = true, ["rawModelText"] = text.Length > 6000 ? text[..6000] : text }
        };
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
}
