using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;
using TaskForge.Tasks.Api.Services.Runtime;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Serialization;

/// <summary>
/// Export builder for the post-split assignment model. Reads type-specific payloads
/// directly from typed spec tables and never rehydrates legacy Assignment columns.
/// </summary>
internal static class AssignmentTypedExportService
{
    internal static async Task<IReadOnlyDictionary<Guid, JsonObject>> LoadImportDtosAsync(
        TasksDbContext db,
        IReadOnlyCollection<Assignment> assignments,
        CancellationToken ct = default, GraphExportOptions? options = null)
    {
        if (assignments.Count == 0) return new Dictionary<Guid, JsonObject>();
        options ??= new GraphExportOptions();
        var content = options.IncludeContent;
        var checks = options.IncludeChecks;
        var ids = assignments.Select(x => x.Id).Distinct().ToArray();
        var code = content || checks ? await db.CodeAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId))
            .Select(x => new CodeAssignmentSpec { AssignmentId = x.AssignmentId,
                Language = content ? x.Language : "", AllowedLanguagesCsv = content ? x.AllowedLanguagesCsv : null,
                StarterCode = content ? x.StarterCode : null, TestsJson = checks ? x.TestsJson : "[]",
                CodeForbiddenCallsJson = checks ? x.CodeForbiddenCallsJson : null,
                CodeRequiredCallsJson = checks ? x.CodeRequiredCallsJson : null }).ToDictionaryAsync(x => x.AssignmentId, ct)
            : new Dictionary<Guid, CodeAssignmentSpec>();
        var image = content || checks ? await db.ImageAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId))
            .Select(x => new ImageAssignmentSpec { AssignmentId = x.AssignmentId,
                Language = content ? x.Language : "", AllowedLanguagesCsv = content ? x.AllowedLanguagesCsv : null,
                StarterCode = content ? x.StarterCode : null, TestsJson = checks ? x.TestsJson : "[]",
                CodeForbiddenCallsJson = checks ? x.CodeForbiddenCallsJson : null,
                CodeRequiredCallsJson = checks ? x.CodeRequiredCallsJson : null }).ToDictionaryAsync(x => x.AssignmentId, ct)
            : new Dictionary<Guid, ImageAssignmentSpec>();
        var test = checks ? await db.TestAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId)).ToDictionaryAsync(x => x.AssignmentId, ct)
            : new Dictionary<Guid, TestAssignmentSpec>();
        var math = checks ? await db.MathAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId)).ToDictionaryAsync(x => x.AssignmentId, ct)
            : new Dictionary<Guid, MathAssignmentSpec>();

        var result = new Dictionary<Guid, JsonObject>(assignments.Count);
        foreach (var assignment in assignments)
        {
            var type = AssignmentTypedReadService.NormalizeStoredType(assignment);
            var dto = Common(assignment, type);
            switch (type)
            {
                case "code-test" when content || checks:
                    if (!code.TryGetValue(assignment.Id, out var codeSpec)) Missing(assignment, type);
                    AddExecutable(dto, codeSpec!.Language, codeSpec.AllowedLanguagesCsv, codeSpec.StarterCode,
                        codeSpec.TestsJson, codeSpec.CodeForbiddenCallsJson, codeSpec.CodeRequiredCallsJson, image: false, content, checks);
                    break;
                case "image-test" when content || checks:
                    if (!image.TryGetValue(assignment.Id, out var imageSpec)) Missing(assignment, type);
                    AddExecutable(dto, imageSpec!.Language, imageSpec.AllowedLanguagesCsv, imageSpec.StarterCode,
                        imageSpec.TestsJson, imageSpec.CodeForbiddenCallsJson, imageSpec.CodeRequiredCallsJson, image: true, content, checks);
                    break;
                case "test" when checks:
                    if (!test.TryGetValue(assignment.Id, out var testSpec)) Missing(assignment, type);
                    dto["testSettings"] = ParseNode(testSpec!.SettingsJson, new JsonObject());
                    dto["questions"] = ParseNode(testSpec.QuestionsJson, new JsonArray());
                    break;
                case "math" when checks:
                    if (!math.TryGetValue(assignment.Id, out var mathSpec)) Missing(assignment, type);
                    dto["testSettings"] = ParseNode(mathSpec!.SettingsJson, new JsonObject());
                    dto["blocks"] = ParseNode(mathSpec.BlocksJson, new JsonArray());
                    break;
                case "sql-test":
                    break;
            }
            result[assignment.Id] = dto;
        }
        return result;
    }

    private static JsonObject Common(Assignment assignment, string type) => new()
    {
        ["id"] = assignment.Id.ToString("D"),
        ["type"] = type,
        ["title"] = assignment.Title,
        ["description"] = assignment.Description ?? string.Empty,
        ["tags"] = assignment.Tags ?? string.Empty,
        ["rating"] = assignment.Rating,
        ["isVisible"] = assignment.IsVisible
    };

    private static void AddExecutable(
        JsonObject dto,
        string language,
        string? allowedLanguagesCsv,
        string? starterCode,
        string testsJson,
        string? forbiddenJson,
        string? requiredJson,
        bool image, bool content, bool checks)
    {
        if (content)
        {
        dto["language"] = language;
        dto["allowedLanguages"] = JsonSerializer.SerializeToNode(ParseCsv(allowedLanguagesCsv, language), JsonOptions());
        dto["starterCode"] = starterCode ?? string.Empty;
        }
        if (!checks) return;
        var imageThreshold = image ? JsonInt(testsJson, "imageTestSimilarityThreshold", 90) : (int?)null;
        dto["testCases"] = ExportTestCasesNode(testsJson, imageThreshold);
        dto["codeForbiddenCalls"] = JsonSerializer.SerializeToNode(ParseStringArrayJson(forbiddenJson), JsonOptions());
        dto["codeRequiredCalls"] = JsonSerializer.SerializeToNode(ParseStringArrayJson(requiredJson), JsonOptions());
        if (!image) return;
        var referenceKey = JsonString(testsJson, "imageTestReferenceKey");
        if (!string.IsNullOrWhiteSpace(referenceKey)) dto["imageTestReferenceKey"] = referenceKey;
        dto["imageTestSimilarityThreshold"] = JsonInt(testsJson, "imageTestSimilarityThreshold", 90);
    }

    private static JsonNode ParseNode(string? raw, JsonNode fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        try { return JsonNode.Parse(raw)?.DeepClone() ?? fallback; }
        catch { throw new InvalidOperationException("DATA_INTEGRITY_ERROR: typed assignment spec contains invalid JSON."); }
    }

    private static void Missing(Assignment assignment, string type)
        => throw new InvalidOperationException($"DATA_INTEGRITY_ERROR: assignment {assignment.Id:D} ({type}) is missing its typed spec.");
}
