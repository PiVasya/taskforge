using System.Text.Json;
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
        var beforeAssignmentId = FindFirstInputAssignmentId(state.Job.Payload);
        var prompt = $$"""
{{TaskForgeAgentPrompts.DraftAuthor}}

Пользователь просит: {{state.UserText}}
План: {{plan}}

Сгенерируй {{count}} TaskForge draft-ов как JSON.
Если пользователь просит "задачки", "обучалки", "серия", "несколько" — верни массив drafts по возрастанию сложности.
Для обучалок по вводу в C# порядок должен быть методическим:
1) Console.ReadLine как строка;
2) строковый ввод в фразе;
3) int.Parse/Convert.ToInt32;
4) несколько значений с разных строк;
5) несколько значений в одной строке через Split.

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
      "publicTests": [{"input":"...","expectedOutput":"...","isHidden":false}],
      "hiddenTests": [{"input":"...","expectedOutput":"...","isHidden":true}],
      "tags": ["AI", "черновик"]
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
            var fallback = BuildFallbackDraftsForInputOnboarding(state.Job, beforeAssignmentId);
            if (fallback.Count == 0) fallback.Add(BuildFallbackDraft(state.Job, attempt, string.Empty));
            ApplyBatchMetadata(fallback, state.Job, beforeAssignmentId);
            state.Draft = fallback.FirstOrDefault();
            return fallback;
        }

        var useDeterministicInputLadder = count > 1 && IsInputOnboardingRequest(state.Job);
        var drafts = useDeterministicInputLadder
            ? BuildFallbackDraftsForInputOnboarding(state.Job, beforeAssignmentId)
            : ParseDrafts(responseText, state.Job);
        if (drafts.Count < Math.Min(2, count) && IsInputOnboardingRequest(state.Job))
            drafts = BuildFallbackDraftsForInputOnboarding(state.Job, beforeAssignmentId);
        if (drafts.Count == 0)
            drafts.Add(BuildFallbackDraft(state.Job, attempt, responseText));

        drafts = NormalizeDraftOrder(drafts, state.Job).Take(count).ToList();
        ApplyBatchMetadata(drafts, state.Job, beforeAssignmentId);
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
            Tags = ReadStringArray(node["tags"]).DefaultIfEmpty("AI").ToList(),
            PublicTests = ReadTests(node["publicTests"] ?? node["tests"], false),
            HiddenTests = ReadTests(node["hiddenTests"], true),
            Extra = new JsonObject { ["rawModelDraft"] = rawText.Length > 6000 ? rawText[..6000] : rawText }
        };
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

    private static List<DraftSpec> BuildFallbackDraftsForInputOnboarding(ClaimedAgentJob job, Guid? beforeAssignmentId)
    {
        if (!IsInputOnboardingRequest(job)) return new List<DraftSpec>();
        return new List<DraftSpec>
        {
            new()
            {
                AssignmentType = "code-test",
                Title = "Ввод строки и вывод её обратно",
                Description = "Условие.\nСчитайте одну строку текста с клавиатуры и выведите её без изменений.\n\nТеория.\nConsole.ReadLine() считывает одну строку и возвращает значение типа string. Его можно сохранить в переменную и затем вывести через Console.WriteLine.\n\nФормат ввода.\nОдна строка текста.\n\nФормат вывода.\nТа же самая строка.\n\nПример.\nВвод:\nHello\nВывод:\nHello",
                Language = "csharp",
                ReferenceSolution = "using System;\n\npublic class Program\n{\n    public static void Main()\n    {\n        string s = Console.ReadLine();\n        Console.WriteLine(s);\n    }\n}\n",
                PublicTests = new List<TestCaseSpec>
                {
                    new() { Input = "Hello\n", ExpectedOutput = "Hello\n" },
                    new() { Input = "C# is cool\n", ExpectedOutput = "C# is cool\n" },
                    new() { Input = "12345\n", ExpectedOutput = "12345\n" }
                },
                HiddenTests = new List<TestCaseSpec>
                {
                    new() { Input = "одна строка\n", ExpectedOutput = "одна строка\n", IsHidden = true },
                    new() { Input = "   пробелы\n", ExpectedOutput = "   пробелы\n", IsHidden = true }
                }
            },
            new()
            {
                AssignmentType = "code-test",
                Title = "Ввод имени и приветствие",
                Description = "Условие.\nСчитайте имя пользователя и выведите приветствие в формате: Привет, имя!\n\nТеория.\nСтроку, полученную через Console.ReadLine(), можно вставлять в другой текст. Для этого удобно использовать интерполяцию строк: $\"Привет, {name}!\".\n\nФормат ввода.\nОдна строка — имя.\n\nФормат вывода.\nПриветствие по образцу.\n\nПример.\nВвод:\nАнна\nВывод:\nПривет, Анна!",
                Language = "csharp",
                ReferenceSolution = "using System;\n\npublic class Program\n{\n    public static void Main()\n    {\n        string name = Console.ReadLine();\n        Console.WriteLine($\"Привет, {name}!\");\n    }\n}\n",
                PublicTests = new List<TestCaseSpec>
                {
                    new() { Input = "Анна\n", ExpectedOutput = "Привет, Анна!\n" },
                    new() { Input = "Ivan\n", ExpectedOutput = "Привет, Ivan!\n" }
                },
                HiddenTests = new List<TestCaseSpec>
                {
                    new() { Input = "Матвей\n", ExpectedOutput = "Привет, Матвей!\n", IsHidden = true },
                    new() { Input = "Оля\n", ExpectedOutput = "Привет, Оля!\n", IsHidden = true }
                }
            },
            new()
            {
                AssignmentType = "code-test",
                Title = "Первый ввод числа: читаем и печатаем обратно",
                Description = "Условие.\nСчитайте одно целое число и выведите его без дополнительных слов.\n\nТеория.\nConsole.ReadLine() возвращает строку. Чтобы получить целое число, используйте int.Parse или Convert.ToInt32.\n\nФормат ввода.\nОдна строка с целым числом.\n\nФормат вывода.\nТо же самое число.\n\nПример.\nВвод:\n7\nВывод:\n7",
                Language = "csharp",
                ReferenceSolution = "using System;\n\npublic class Program\n{\n    public static void Main()\n    {\n        int x = int.Parse(Console.ReadLine());\n        Console.WriteLine(x);\n    }\n}\n",
                PublicTests = new List<TestCaseSpec>
                {
                    new() { Input = "7\n", ExpectedOutput = "7\n" },
                    new() { Input = "-15\n", ExpectedOutput = "-15\n" },
                    new() { Input = "0\n", ExpectedOutput = "0\n" }
                },
                HiddenTests = new List<TestCaseSpec>
                {
                    new() { Input = "123\n", ExpectedOutput = "123\n", IsHidden = true },
                    new() { Input = "-999\n", ExpectedOutput = "-999\n", IsHidden = true }
                }
            },
            new()
            {
                AssignmentType = "code-test",
                Title = "Сумма двух чисел с разных строк",
                Description = "Условие.\nСчитайте два целых числа. Каждое число вводится с новой строки. Выведите их сумму.\n\nТеория.\nЕсли нужно считать несколько строк, Console.ReadLine() вызывается несколько раз. Каждую строку с числом нужно преобразовать в int.\n\nФормат ввода.\nДве строки, в каждой по одному целому числу.\n\nФормат вывода.\nСумма двух чисел.\n\nПример.\nВвод:\n2\n3\nВывод:\n5",
                Language = "csharp",
                ReferenceSolution = "using System;\n\npublic class Program\n{\n    public static void Main()\n    {\n        int a = int.Parse(Console.ReadLine());\n        int b = int.Parse(Console.ReadLine());\n        Console.WriteLine(a + b);\n    }\n}\n",
                PublicTests = new List<TestCaseSpec>
                {
                    new() { Input = "2\n3\n", ExpectedOutput = "5\n" },
                    new() { Input = "10\n-4\n", ExpectedOutput = "6\n" }
                },
                HiddenTests = new List<TestCaseSpec>
                {
                    new() { Input = "0\n0\n", ExpectedOutput = "0\n", IsHidden = true },
                    new() { Input = "-5\n12\n", ExpectedOutput = "7\n", IsHidden = true }
                }
            },
            new()
            {
                AssignmentType = "code-test",
                Title = "Два числа в одной строке через Split",
                Description = "Условие.\nСчитайте два целых числа, записанных в одной строке через пробел, и выведите их сумму.\n\nТеория.\nОдну строку можно разделить на части методом Split(' '). После этого каждую часть можно преобразовать в int.\n\nФормат ввода.\nОдна строка с двумя целыми числами через пробел.\n\nФормат вывода.\nСумма двух чисел.\n\nПример.\nВвод:\n2 3\nВывод:\n5",
                Language = "csharp",
                ReferenceSolution = "using System;\n\npublic class Program\n{\n    public static void Main()\n    {\n        string[] p = Console.ReadLine().Split(' ');\n        int a = int.Parse(p[0]);\n        int b = int.Parse(p[1]);\n        Console.WriteLine(a + b);\n    }\n}\n",
                PublicTests = new List<TestCaseSpec>
                {
                    new() { Input = "2 3\n", ExpectedOutput = "5\n" },
                    new() { Input = "10 -4\n", ExpectedOutput = "6\n" }
                },
                HiddenTests = new List<TestCaseSpec>
                {
                    new() { Input = "-5 -7\n", ExpectedOutput = "-12\n", IsHidden = true },
                    new() { Input = "0 0\n", ExpectedOutput = "0\n", IsHidden = true }
                }
            }
        };
    }

    private static void ApplyBatchMetadata(List<DraftSpec> drafts, ClaimedAgentJob job, Guid? beforeAssignmentId)
    {
        for (var i = 0; i < drafts.Count; i++)
        {
            drafts[i].CourseId = job.CourseId;
            drafts[i].BeforeAssignmentId ??= beforeAssignmentId;
            drafts[i].SourceTaskIndex = i;
            drafts[i].Tags = MergeTags(drafts[i].Tags, new[] { "AI", "черновик" });
            drafts[i].Language = NormalizeLanguage(drafts[i].Language);
            drafts[i].Difficulty = Math.Clamp(drafts[i].Difficulty, 1, 3);
            drafts[i].Rating = Math.Max(1, drafts[i].Rating);
        }
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

    private static List<DraftSpec> NormalizeDraftOrder(List<DraftSpec> drafts, ClaimedAgentJob job)
    {
        if (!IsInputOnboardingRequest(job)) return drafts;
        return drafts
            .Select((draft, index) => new { draft, index, rank = InputDraftRank(draft) })
            .OrderBy(x => x.rank)
            .ThenBy(x => x.index)
            .Select(x => x.draft)
            .ToList();
    }

    private static int InputDraftRank(DraftSpec draft)
    {
        var text = $"{draft.Title} {draft.Description}".ToLowerInvariant();
        if (text.Contains("split")) return 50;
        if (text.Contains("с разных строк") || text.Contains("две строки") || text.Contains("два целых")) return 40;
        if (text.Contains("int.parse") || text.Contains("convert.toint32") || text.Contains("целое число") || text.Contains("числа")) return 30;
        if (text.Contains("имя") || text.Contains("привет")) return 20;
        if (text.Contains("строк")) return 10;
        return 100;
    }

    private static bool IsInputOnboardingRequest(ClaimedAgentJob job)
    {
        var text = job.UserText.ToLowerInvariant();
        return text.Contains("ввод") || text.Contains("readline") || text.Contains("input") || text.Contains("с клавиатур");
    }

    public static bool LooksLikeMultipleDraftRequest(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("задачки") || t.Contains("обучалки") || t.Contains("несколько") || t.Contains("серия") || t.Contains("набор") || t.Contains("лестниц") || t.Contains("guided ladder");
    }

    private static Guid? FindFirstInputAssignmentId(JsonElement payload)
    {
        var candidates = new List<AssignmentAnchorCandidate>();
        CollectAssignmentCandidates(payload, candidates, 0);
        return candidates
            .Where(x => !x.IsHidden && !x.IsAiDraft && IsExistingInputTask(x.Text))
            .OrderBy(x => x.Index ?? int.MaxValue)
            .ThenBy(x => x.Sort ?? int.MaxValue)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefault();
    }

    private static bool IsExistingInputTask(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("console.readline")
               || t.Contains("readline")
               || t.Contains("стандартного ввода")
               || t.Contains("с клавиатур")
               || t.Contains("считайте")
               || t.Contains("считать")
               || t.Contains("вводится")
               || t.Contains("введите");
    }

    private static void CollectAssignmentCandidates(JsonElement element, List<AssignmentAnchorCandidate> result, int depth)
    {
        if (depth > 10) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var id = GetGuid(element, "id", "assignmentId");
            var title = GetString(element, "title", "name");
            var description = GetString(element, "description", "descriptionPreview", "condition", "body");
            var tags = GetString(element, "tags");
            if (id.HasValue && !string.IsNullOrWhiteSpace(title) && (HasAnyProperty(element, "sort", "index", "type", "assignmentType", "description", "descriptionPreview", "tags")))
            {
                result.Add(new AssignmentAnchorCandidate(
                    id.Value,
                    GetInt(element, "index"),
                    GetInt(element, "sort", "order"),
                    $"{title} {description} {tags}",
                    GetBool(element, "isHidden") == true,
                    GetBool(element, "isAiDraft") == true));
            }

            foreach (var prop in element.EnumerateObject())
                CollectAssignmentCandidates(prop.Value, result, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectAssignmentCandidates(item, result, depth + 1);
        }
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

    private static string? GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            if (prop.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return prop.ToString();
        }
        return null;
    }

    private static Guid? GetGuid(JsonElement element, params string[] names)
    {
        var raw = GetString(element, names);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n)) return n;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static bool? GetBool(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static bool HasAnyProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in names)
            if (element.TryGetProperty(name, out _)) return true;
        return false;
    }

    private sealed record AssignmentAnchorCandidate(Guid Id, int? Index, int? Sort, string Text, bool IsHidden, bool IsAiDraft);
}
