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

}
