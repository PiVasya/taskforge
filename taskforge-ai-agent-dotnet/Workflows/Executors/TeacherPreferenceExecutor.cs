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
            var merged = MergeDefaults(defaults, parsed);
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
