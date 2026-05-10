using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed class ApprovalGateExecutor
{
    private readonly AgentStepReporter _steps;
    public ApprovalGateExecutor(AgentStepReporter steps) => _steps = steps;

    public async Task ExecuteForDraftAsync(WorkflowState state, DraftSpec draft)
        => await ExecuteForDraftAsync(state, draft, "assignment_draft_ready", "save_hidden_draft", "Требуется подтверждение сохранения черновика");

    public async Task ExecuteForPolishedDraftAsync(WorkflowState state, DraftSpec draft)
        => await ExecuteForDraftAsync(state, draft, "polished_assignment_draft", "save_hidden_polished_draft", "Подготовлен скрытый вылизанный черновик");

    private async Task ExecuteForDraftAsync(WorkflowState state, DraftSpec draft, string artifactType, string operation, string title)
    {
        var artifactData = draft.ToArtifactData();
        state.Data.TryGetPropertyValue("draftShapeValidation", out var shape);
        state.Data.TryGetPropertyValue("testRun", out var testRun);
        state.Data.TryGetPropertyValue("critique", out var critique);
        var validationOk = critique is JsonObject critiqueObject
            && string.Equals(critiqueObject["isAccepted"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        artifactData["validation"] = new JsonObject
        {
            ["shape"] = shape?.DeepClone(),
            ["tests"] = testRun?.DeepClone(),
            ["critique"] = critique?.DeepClone(),
            ["ok"] = validationOk
        };
        state.Artifacts.Add(new AgentArtifact(artifactType, draft.Title, artifactData));
        state.Artifacts.Add(new AgentArtifact("approval_request", title, new JsonObject
        {
            ["operation"] = operation,
            ["reason"] = "AI подготовил задание. Оно сохраняется только как скрытый draft и требует ручной проверки перед публикацией.",
            ["payload"] = artifactData.DeepClone(),
            ["requiresHumanApproval"] = true,
            ["autoPublish"] = false
        }));
        state.RequiresApproval = true;
        await _steps.TryReportAsync("approval", "completed", "Подготовлен draft artifact", "Черновик не публикуется автоматически; backend может создать только скрытый draft из artifact после завершения run.", artifactData);
    }

    public async Task ExecuteForCourseEditAsync(WorkflowState state, JsonObject patch)
    {
        state.Artifacts.Add(new AgentArtifact("course_edit_proposal", "Пакет правок курса", patch));
        state.Artifacts.Add(new AgentArtifact("approval_request", "Требуется подтверждение правок курса", new JsonObject
        {
            ["operation"] = "apply_course_edit",
            ["reason"] = "Правки изменяют реальные задания курса и требуют отдельного подтверждения пользователя перед применением.",
            ["payload"] = patch.DeepClone(),
            ["requiresHumanApproval"] = true,
            ["autoApply"] = false
        }));
        state.RequiresApproval = true;
        await _steps.TryReportAsync("approval", "completed", "Пакет правок вынесен на подтверждение", null, patch);
    }
}
