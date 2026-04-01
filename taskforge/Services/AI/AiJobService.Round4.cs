using System.Text.Json;
using System.Text.Json.Nodes;

namespace taskforge.Services.AI;

public sealed partial class AiJobService
{
    private static string MergeDraftValidation(string? existingDraftJson, JsonElement validationRoot)
    {
        JsonNode rootNode;
        try
        {
            rootNode = string.IsNullOrWhiteSpace(existingDraftJson)
                ? new JsonObject()
                : JsonNode.Parse(existingDraftJson!) ?? new JsonObject();
        }
        catch
        {
            rootNode = new JsonObject();
        }

        if (rootNode is not JsonObject rootObj)
            rootObj = new JsonObject();

        var meta = rootObj["meta"] as JsonObject ?? new JsonObject();
        meta["selfCheck"] = JsonNode.Parse(validationRoot.GetRawText());
        rootObj["meta"] = meta;
        return rootObj.ToJsonString(JsonOptions);
    }
}
