using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Specs;
using static TaskForge.Tasks.Api.Services.Authoring.AssignmentAuthoringCommonService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Authoring;

internal static class MathAssignmentAuthoringService
{
    internal static async Task SaveAsync(TasksDbContext db, Assignment assignment, MathAssignmentAuthoringRequest request, CancellationToken ct)
    {
        var current = await AssignmentTypeSpecService.ReadMathAsync(db, assignment, ct);
        var node = MathSpecToJsonObject(current);
        if (request.TestSettings.HasValue)
            node["settings"] = JsonNode.Parse(request.TestSettings.Value.GetRawText());
        if (request.Blocks.HasValue)
            node["blocks"] = JsonNode.Parse(request.Blocks.Value.GetRawText());
        await AssignmentTypeSpecService.SaveMathPayloadAsync(db, assignment, JsonSerializer.SerializeToElement(node, JsonOptions()), ct);
    }

    internal static async Task<JsonObject> BuildEditDtoAsync(TasksDbContext db, Assignment assignment, CancellationToken ct)
    {
        var spec = await AssignmentTypeSpecService.ReadMathAsync(db, assignment, ct);
        var specNode = MathSpecToJsonObject(spec);
        var node = BuildCommonEditNode(assignment, "math");
        node["testSettings"] = specNode["settings"]?.DeepClone();
        node["blocks"] = specNode["blocks"]?.DeepClone();
        return node;
    }
}
