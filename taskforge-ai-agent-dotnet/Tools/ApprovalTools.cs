using System.ComponentModel;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Safety;

namespace TaskForge.AiAgent.Tools;

public sealed class ApprovalTools
{
    private readonly WriteOperationGuard _guard;
    public ApprovalTools(WriteOperationGuard guard) => _guard = guard;

    [Description("Create a human approval request for a sensitive operation. Use this before saving drafts, applying course edits or publishing anything.")]
    public Task<JsonObject> RequestHumanApprovalAsync(
        [Description("Operation name, e.g. save_hidden_draft, apply_course_edit, publish_assignment.")] string operation,
        [Description("Why the user must approve this operation.")] string reason,
        [Description("Payload that would be applied if approved.")] JsonObject payload)
    {
        var request = _guard.RequireApproval(operation, reason, payload);
        return Task.FromResult(new JsonObject
        {
            ["operation"] = request.Operation,
            ["reason"] = request.Reason,
            ["payload"] = request.Payload.DeepClone(),
            ["requiresHumanApproval"] = true,
            ["status"] = "waiting_approval"
        });
    }
}
