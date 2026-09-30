using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Specs;
using static TaskForge.Tasks.Api.Services.Authoring.AssignmentAuthoringCommonService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Authoring;

internal static class ImageAssignmentAuthoringService
{
    internal static async Task SaveAsync(
        TasksDbContext db,
        Assignment assignment,
        ImageAssignmentAuthoringRequest request,
        IHttpClientFactory clients,
        IConfiguration cfg,
        CancellationToken ct)
    {
        var current = await AssignmentTypeSpecService.ReadImageAsync(db, assignment, ct);
        var incoming = RawJson(request.TestCases);
        var hasRealSpec = HasMeaningfulJsonElement(request.TestCases);
        var testsJson = current.TestsJson;
        if (hasRealSpec || request.ImageTestReferenceKey is not null || request.ImageTestSimilarityThreshold.HasValue)
        {
            testsJson = (await TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService.MergeAndMaterializeImageTestPayloadAsync(
                hasRealSpec ? incoming : current.TestsJson,
                request.ImageTestReferenceKey,
                request.ImageTestSimilarityThreshold,
                assignment.Id,
                clients,
                cfg,
                ct)).ToJsonString(JsonOptions());
        }

        var language = NormalizeLanguage(request.Language) ?? current.Language;
        var allowed = request.AllowedLanguages is null ? current.AllowedLanguagesCsv : NormalizeLanguagesCsv(request.AllowedLanguages);
        var starter = request.StarterCode ?? current.StarterCode;
        var forbidden = request.CodeForbiddenCalls is null ? current.CodeForbiddenCallsJson : StringArrayJson(request.CodeForbiddenCalls);
        var required = request.CodeRequiredCalls is null ? current.CodeRequiredCallsJson : StringArrayJson(request.CodeRequiredCalls);

        var row = db.ImageAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.ImageAssignmentSpecs.FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is null)
        {
            row = new ImageAssignmentSpec { AssignmentId = assignment.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.ImageAssignmentSpecs.Add(row);
        }
        row.Language = language;
        row.AllowedLanguagesCsv = allowed;
        row.StarterCode = starter;
        row.TestsJson = string.IsNullOrWhiteSpace(testsJson) ? "{}" : testsJson;
        row.CodeForbiddenCallsJson = forbidden;
        row.CodeRequiredCallsJson = required;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        assignment.Language = language;
        ClearLegacyPayload(assignment, keepLanguage: true);
    }

    internal static async Task<JsonObject> BuildEditDtoAsync(TasksDbContext db, Assignment assignment, CancellationToken ct)
    {
        var spec = await AssignmentTypeSpecService.ReadImageAsync(db, assignment, ct);
        var threshold = JsonInt(spec.TestsJson, "imageTestSimilarityThreshold", 90);
        var node = BuildCommonEditNode(assignment, "image-test");
        node["language"] = spec.Language;
        node["allowedLanguages"] = JsonSerializer.SerializeToNode(ParseCsv(spec.AllowedLanguagesCsv, spec.Language), JsonOptions());
        node["starterCode"] = spec.StarterCode ?? string.Empty;
        node["testCases"] = ExportTestCasesNode(spec.TestsJson, threshold);
        node["tests"] = JsonNode.Parse(spec.TestsJson);
        node["codeForbiddenCalls"] = JsonSerializer.SerializeToNode(ParseStringArrayJson(spec.CodeForbiddenCallsJson), JsonOptions());
        node["codeRequiredCalls"] = JsonSerializer.SerializeToNode(ParseStringArrayJson(spec.CodeRequiredCallsJson), JsonOptions());
        node["imageTestReferenceKey"] = JsonString(spec.TestsJson, "imageTestReferenceKey");
        node["imageTestSimilarityThreshold"] = threshold;
        return node;
    }
}
