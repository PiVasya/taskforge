using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Workflows;

namespace TaskForge.AiAgent.Workflows.Executors;

/// <summary>
/// Extracts the teacher's generation preferences before any course analysis.
/// This is intentionally separate from draft generation: the agent first learns
/// the user's desired pedagogy/style, then uses that profile in skill mapping,
/// planning, generation and critique.
/// </summary>
public sealed class TeacherPreferenceExecutor
{
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private AIAgent? _agent;

    public TeacherPreferenceExecutor(TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps)
    {
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
    }

    public async Task<JsonObject> ExecuteAsync(WorkflowState state, string contextPrompt, CancellationToken cancellationToken)
    {
        await _steps.TryReportAsync(
            "teacher_preferences",
            "running",
            "Уточняю педагогические предпочтения",
            "Агент отделяет стиль преподавателя от конкретного тестового запроса и сохраняет это как правила работы.");

        var defaults = BuildDefaultProfile();
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        var prompt = $$"""
{{TaskForgeAgentPrompts.Coordinator}}

Ты не генерируешь задания. Сначала извлеки предпочтения преподавателя из текущего сообщения, памяти и истории контекста.
Это НЕ предметный hardcode и НЕ лестница задач. Нужно описать, как агент должен думать при любых темах курса.

Текущий запрос пользователя:
{{state.UserText}}

Контекст и память TaskForge:
{{contextPrompt}}

Существующий безопасный профиль по умолчанию:
{{defaults.ToJsonString()}}

Верни строго JSON без markdown:
{
  "teachingPreferences": {
    "courseUnderstandingFirst": true,
    "agentBuildsSkillMapItself": true,
    "avoidHardcodedTopicLadders": true,
    "avoidRegexOrKeywordAnchorsAsSourceOfTruth": true,
    "oneNewSkillPerBridgeTask": true,
    "preferMicroStepsForLearningTasks": true,
    "doNotUseFutureSkills": true,
    "respectKnownStudentSkills": true,
    "studentFacingTextOnly": true,
    "noInternalMetadataInTitleOrDescription": true,
    "noInternalTagsInCourseCards": true,
    "inlineCodeWithBackticks": true,
    "generateOnlyIfCourseSkillMapSupportsIt": true
  },
  "styleRules": ["короткие правила формулировки заданий"],
  "qualityGateRules": ["что критик обязан отклонять"],
  "memoryNotes": ["что стоит запомнить для следующих запусков"],
  "confidence": 0.0
}
""";

        try
        {
            var session = await _sessionStore.LoadAsync(_agent, state.Job.ConversationId, cancellationToken);
            var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);
            var parsed = ParseProfile(response.Text ?? string.Empty) ?? defaults;
            state.Data["requestSpecificTeacherProfileRaw"] = parsed.DeepClone();
            var merged = SanitizeGenericProfile(MergeDefaults(defaults, parsed), defaults);
            SaveToState(state, merged, "llm");
            await _steps.TryReportAsync("teacher_preferences", "completed", "Педагогический профиль обновлён", "Агент будет опираться на карту навыков курса, а не на зашитую тему.", merged);
            return merged;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.Notes.Add($"Teacher preference extraction failed: {ex.GetType().Name}: {ex.Message}. Using safe default profile.");
            defaults["source"] = "safe-default";
            defaults["error"] = ex.Message;
            SaveToState(state, defaults, "safe-default");
            await _steps.TryReportAsync("teacher_preferences", "completed", "Использую безопасный профиль преподавателя", ex.Message, defaults);
            return defaults;
        }
    }

    private static void SaveToState(WorkflowState state, JsonObject profile, string source)
    {
        profile["source"] ??= source;
        state.TeacherPreferences = profile.DeepClone().AsObject();
        state.Data["teacherPreferences"] = profile.DeepClone();
        state.MemoryPatch["teachingPreferences"] = profile.DeepClone();
    }

    private static JsonObject BuildDefaultProfile()
    {
        return new JsonObject
        {
            ["teachingPreferences"] = new JsonObject
            {
                ["courseUnderstandingFirst"] = true,
                ["agentBuildsSkillMapItself"] = true,
                ["avoidHardcodedTopicLadders"] = true,
                ["avoidRegexOrKeywordAnchorsAsSourceOfTruth"] = true,
                ["oneNewSkillPerBridgeTask"] = true,
                ["preferMicroStepsForLearningTasks"] = true,
                ["doNotUseFutureSkills"] = true,
                ["respectKnownStudentSkills"] = true,
                ["studentFacingTextOnly"] = true,
                ["noInternalMetadataInTitleOrDescription"] = true,
                ["noInternalTagsInCourseCards"] = true,
                ["inlineCodeWithBackticks"] = true,
                ["generateOnlyIfCourseSkillMapSupportsIt"] = true
            },
            ["styleRules"] = new JsonArray(
                "Обучалка должна быть проще ближайшего целевого задания.",
                "Каждое bridge-задание вводит один маленький новый навык.",
                "Не использовать в условии темы, которые студент ещё не проходил.",
                "Связь с местом в курсе хранить в metadata, а не в тексте для студента.",
                "Кодовые элементы писать в `backticks`, чтобы редактор показал фон."),
            ["qualityGateRules"] = new JsonArray(
                "Отклонять задания, если они основаны на keyword/regex-якоре вместо COURSE_SKILL_MAP.",
                "Отклонять задания, если они используют будущие навыки из mustNotUse.",
                "Отклонять задания, если title/description содержит внутренние служебные фразы.",
                "Отклонять learning-bridge, если нет явного introducedSkills или bridgePlan step."),
            ["memoryNotes"] = new JsonArray(
                "Пользователь хочет, чтобы агент сам строил карту навыков курса и не хардкодил конкретные темы."),
            ["confidence"] = 0.75,
            ["source"] = "safe-default"
        };
    }

    private static JsonObject MergeDefaults(JsonObject defaults, JsonObject parsed)
    {
        var result = defaults.DeepClone().AsObject();
        foreach (var kvp in parsed)
            result[kvp.Key] = kvp.Value?.DeepClone();

        if (result["teachingPreferences"] is not JsonObject prefs)
        {
            result["teachingPreferences"] = defaults["teachingPreferences"]?.DeepClone();
        }
        else if (defaults["teachingPreferences"] is JsonObject defaultPrefs)
        {
            foreach (var kvp in defaultPrefs)
                prefs[kvp.Key] ??= kvp.Value?.DeepClone();
        }

        result["styleRules"] ??= defaults["styleRules"]?.DeepClone();
        result["qualityGateRules"] ??= defaults["qualityGateRules"]?.DeepClone();
        result["memoryNotes"] ??= defaults["memoryNotes"]?.DeepClone();
        result["confidence"] ??= defaults["confidence"]?.DeepClone();
        return result;
    }

    private static JsonObject SanitizeGenericProfile(JsonObject profile, JsonObject defaults)
    {
        var result = defaults.DeepClone().AsObject();

        if (profile["confidence"] is not null)
            result["confidence"] = profile["confidence"]?.DeepClone();
        result["source"] = profile["source"]?.DeepClone() ?? JsonValue.Create("llm");

        if (profile["teachingPreferences"] is JsonObject parsedPrefs && result["teachingPreferences"] is JsonObject prefs)
        {
            var allowed = new HashSet<string>(prefs.Select(kvp => kvp.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in parsedPrefs)
            {
                if (allowed.Contains(kvp.Key))
                    prefs[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        // Never persist request-specific topic plans in teacher memory. The user text
        // remains available to the current workflow, but preferences should describe
        // how the agent thinks for any topic, not a ladder for today's topic.
        result["styleRules"] = FilterGenericRuleArray(profile["styleRules"], defaults["styleRules"]);
        result["qualityGateRules"] = FilterGenericRuleArray(profile["qualityGateRules"], defaults["qualityGateRules"]);
        result["memoryNotes"] = FilterGenericRuleArray(profile["memoryNotes"], defaults["memoryNotes"]);
        return result;
    }

    private static JsonArray FilterGenericRuleArray(JsonNode? parsed, JsonNode? fallback)
    {
        var arr = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!seen.Add(text.Trim())) return;
            arr.Add(text.Trim());
        }

        if (fallback is JsonArray fallbackArr)
        {
            foreach (var item in fallbackArr)
                Add(item?.ToString());
        }

        if (parsed is JsonArray parsedArr)
        {
            foreach (var item in parsedArr)
            {
                var text = item?.ToString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (LooksRequestSpecificRule(text)) continue;
                Add(text);
            }
        }

        return arr;
    }

    private static bool LooksRequestSpecificRule(string text)
    {
        var t = text.ToLowerInvariant();
        // Generic preferences should not contain concrete APIs, method calls, type
        // names or numbered task ladders. Those belong in the current request plan,
        // not in long-lived teacher memory.
        if (t.Contains("task a") || t.Contains("task b") || t.Contains("task c")) return true;
        if (t.Contains("suggested") || t.Contains("preferredinput") || t.Contains("preferred input")) return true;
        if (t.Contains("`") || t.Contains("()") || t.Contains("<") && t.Contains(">")) return true;
        if (t.Contains("console.") || t.Contains("parse") || t.Contains("tryparse") || t.Contains("split") || t.Contains("linq")) return true;
        if (t.Contains("double") || t.Contains("float") || t.Contains("int ") || t.Contains("string ") || t.Contains("bool")) return true;
        return false;
    }

    private static JsonObject? ParseProfile(string text)
    {
        try
        {
            var json = ExtractJson(text);
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Trim();
        if (clean.StartsWith("```", StringComparison.Ordinal))
        {
            var first = clean.IndexOf('\n');
            var last = clean.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first) clean = clean[(first + 1)..last].Trim();
        }

        var start = clean.IndexOf('{');
        var end = clean.LastIndexOf('}');
        return start >= 0 && end > start ? clean[start..(end + 1)] : null;
    }
}
