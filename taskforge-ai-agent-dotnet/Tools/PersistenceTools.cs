using System.ComponentModel;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Safety;

namespace TaskForge.AiAgent.Tools;

public sealed class PersistenceTools
{
    private readonly WriteOperationGuard _guard;
    public PersistenceTools(WriteOperationGuard guard) => _guard = guard;

    [Description("Dangerous direct write hook for saving a hidden assignment draft. Disabled by default; normal flow must use artifacts + backend approval.")]
    public Task<JsonObject> SaveHiddenDraftDirectAsync(JsonObject draft)
    {
        _guard.ThrowIfDangerousWritesAreDisabled("save_hidden_draft_direct");
        return Task.FromResult(new JsonObject
        {
            ["ok"] = false,
            ["reason"] = "Direct persistence is intentionally not implemented in the worker. Backend owns writes."
        });
    }
}
