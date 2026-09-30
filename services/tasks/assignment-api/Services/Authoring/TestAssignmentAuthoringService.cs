using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Testing;
using static TaskForge.Tasks.Api.Services.Authoring.AssignmentAuthoringCommonService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Authoring;

internal static class TestAssignmentAuthoringService
{
    internal static async Task SaveAsync(TasksDbContext db, Assignment assignment, TestAssignmentAuthoringRequest request, CancellationToken ct)
    {
        if (!request.TestSettings.HasValue && !request.Questions.HasValue)
        {
            await TestAssignmentSpecService.EnsureDetachedAsync(db, assignment, ct);
            return;
        }

        var current = await TestAssignmentSpecService.ReadAsync(db, assignment, ct);
        var node = TaskSpecToJsonObject(current);
        if (request.TestSettings.HasValue)
            node["settings"] = JsonNode.Parse(request.TestSettings.Value.GetRawText());
        if (request.Questions.HasValue)
            node["questions"] = JsonNode.Parse(request.Questions.Value.GetRawText());
        await TestAssignmentSpecService.SaveAsync(db, assignment, JsonSerializer.SerializeToElement(node, JsonOptions()), ct);
    }

    internal static async Task<JsonObject> BuildEditDtoAsync(TasksDbContext db, Assignment assignment, CancellationToken ct)
    {
        var spec = await TestAssignmentSpecService.ReadAsync(db, assignment, ct);
        var specNode = TaskSpecToJsonObject(spec);
        var node = BuildCommonEditNode(assignment, "test");
        node["testSettings"] = specNode["settings"]?.DeepClone();
        node["questions"] = specNode["questions"]?.DeepClone();
        return node;
    }
}
