using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Tools;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed class DraftValidationExecutor
{
    private readonly ValidationTools _validationTools;
    private readonly AgentStepReporter _steps;

    public DraftValidationExecutor(ValidationTools validationTools, AgentStepReporter steps)
    {
        _validationTools = validationTools;
        _steps = steps;
    }

    public async Task<JsonObject> ExecuteAsync(WorkflowState state, DraftSpec draft)
    {
        await _steps.TryReportAsync("validation", "running", "Проверяю структуру черновика", draft.Title);
        var shape = await _validationTools.ValidateDraftShapeAsync(draft.ToArtifactData());
        state.Data["draftShapeValidation"] = shape.DeepClone();

        if (draft.AssignmentType == "code-test" && !string.IsNullOrWhiteSpace(draft.ReferenceSolution) && draft.PublicTests.Concat(draft.HiddenTests).Any())
        {
            var tests = draft.PublicTests.Concat(draft.HiddenTests).ToList();
            var run = await _validationTools.RunCodeTestsAsync(draft.Language, draft.ReferenceSolution, tests);
            state.Data["testRun"] = run.DeepClone();
            await _steps.TryReportAsync("validation", run["ok"]?.ToString() == "True" || run["ok"]?.ToString() == "true" ? "completed" : "failed", "Тесты черновика прогнаны", $"Всего тестов: {tests.Count}", run);
            return new JsonObject
            {
                ["shape"] = shape.DeepClone(),
                ["tests"] = run.DeepClone(),
                ["ok"] = IsOk(shape) && IsOk(run)
            };
        }

        await _steps.TryReportAsync("validation", IsOk(shape) ? "completed" : "failed", "Структура черновика проверена", null, shape);
        return new JsonObject
        {
            ["shape"] = shape.DeepClone(),
            ["ok"] = IsOk(shape)
        };
    }

    private static bool IsOk(JsonObject obj)
    {
        var value = obj["ok"]?.ToString();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
