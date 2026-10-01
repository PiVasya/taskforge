using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Analytics;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Runtime;

internal static class AssignmentTypedReadService
{
    private sealed record SpecBundle(
        CodeAssignmentSpec? Code = null,
        ImageAssignmentSpec? Image = null,
        TestAssignmentSpec? Test = null,
        MathAssignmentSpec? Math = null);

    internal static async Task<IReadOnlyList<JsonObject>> BuildDtosAsync(
        TasksDbContext db,
        IReadOnlyCollection<Assignment> assignments,
        bool includeSensitive,
        IReadOnlySet<Guid>? solvedIds = null,
        CancellationToken ct = default)
    {
        if (assignments.Count == 0) return Array.Empty<JsonObject>();
        var ids = assignments.Select(x => x.Id).Distinct().ToArray();
        var code = await db.CodeAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId)).ToDictionaryAsync(x => x.AssignmentId, ct);
        var image = await db.ImageAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId)).ToDictionaryAsync(x => x.AssignmentId, ct);
        var test = await db.TestAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId)).ToDictionaryAsync(x => x.AssignmentId, ct);
        var math = await db.MathAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId)).ToDictionaryAsync(x => x.AssignmentId, ct);

        return assignments.Select(assignment =>
        {
            var type = NormalizeStoredType(assignment);
            var bundle = new SpecBundle(
                code.GetValueOrDefault(assignment.Id),
                image.GetValueOrDefault(assignment.Id),
                test.GetValueOrDefault(assignment.Id),
                math.GetValueOrDefault(assignment.Id));
            EnsureSpecPresent(assignment, type, bundle);
            return BuildDto(assignment, type, bundle, includeSensitive, solvedIds?.Contains(assignment.Id) == true);
        }).ToArray();
    }

    internal static async Task<JsonObject> BuildDtoAsync(
        TasksDbContext db,
        Assignment assignment,
        bool includeSensitive,
        bool isSolved = false,
        CancellationToken ct = default)
    {
        var type = NormalizeStoredType(assignment);
        var bundle = await LoadBundleAsync(db, assignment.Id, type, ct);
        EnsureSpecPresent(assignment, type, bundle);
        return BuildDto(assignment, type, bundle, includeSensitive, isSolved);
    }

    internal static async Task<JsonObject> BuildSolveShellAsync(
        TasksDbContext db,
        Assignment assignment,
        bool includeSensitive,
        bool isSolved,
        string routePrefix,
        CancellationToken ct = default)
    {
        var type = NormalizeStoredType(assignment);
        var bundle = await LoadBundleAsync(db, assignment.Id, type, ct);
        EnsureSpecPresent(assignment, type, bundle);
        var node = BuildCommon(assignment, type, includeSensitive, isSolved, includeDescription: false);
        AddExecutableFields(node, type, bundle, includeSensitive, includeTests: false);
        node["parts"] = new JsonObject
        {
            ["statementUrl"] = $"{routePrefix}/{assignment.Id:D}/statement",
            ["testsUrl"] = $"{routePrefix}/{assignment.Id:D}/tests"
        };
        return node;
    }

    internal static async Task<JsonObject> BuildStatementAsync(
        TasksDbContext db,
        Assignment assignment,
        bool includeSensitive,
        CancellationToken ct = default)
    {
        var type = NormalizeStoredType(assignment);
        var bundle = await LoadBundleAsync(db, assignment.Id, type, ct);
        EnsureSpecPresent(assignment, type, bundle);
        var node = new JsonObject
        {
            ["id"] = assignment.Id,
            ["courseId"] = assignment.CourseId,
            ["title"] = assignment.Title,
            ["description"] = assignment.Description ?? string.Empty,
            ["tags"] = assignment.Tags ?? string.Empty,
            ["rating"] = assignment.Rating,
            ["canEdit"] = includeSensitive,
            ["updatedAt"] = assignment.UpdatedAt
        };
        node["taskConstraints"] = BuildTaskConstraints(type, bundle);
        return node;
    }

    internal static async Task<JsonObject> BuildTestsAsync(
        TasksDbContext db,
        Assignment assignment,
        bool includeSensitive,
        CancellationToken ct = default)
    {
        var type = NormalizeStoredType(assignment);
        var bundle = await LoadBundleAsync(db, assignment.Id, type, ct);
        EnsureSpecPresent(assignment, type, bundle);
        var node = new JsonObject
        {
            ["id"] = assignment.Id,
            ["type"] = type,
            ["canEdit"] = includeSensitive,
            ["updatedAt"] = assignment.UpdatedAt
        };
        AddTestsFields(node, type, bundle, includeSensitive);
        return node;
    }

    internal static string NormalizeStoredType(Assignment assignment)
    {
        if (TryNormalizeAssignmentType(assignment.Type, out var normalized)) return normalized;
        throw new InvalidOperationException($"DATA_INTEGRITY_ERROR: assignment {assignment.Id:D} has unsupported stored type '{assignment.Type}'.");
    }

    private static async Task<SpecBundle> LoadBundleAsync(TasksDbContext db, Guid assignmentId, string type, CancellationToken ct)
        => type switch
        {
            "code-test" => new SpecBundle(Code: await db.CodeAssignmentSpecs.AsNoTracking().SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct)),
            "image-test" => new SpecBundle(Image: await db.ImageAssignmentSpecs.AsNoTracking().SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct)),
            "test" => new SpecBundle(Test: await db.TestAssignmentSpecs.AsNoTracking().SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct)),
            "math" => new SpecBundle(Math: await db.MathAssignmentSpecs.AsNoTracking().SingleOrDefaultAsync(x => x.AssignmentId == assignmentId, ct)),
            "sql-test" => new SpecBundle(),
            _ => throw new InvalidOperationException($"Unsupported assignment type '{type}'.")
        };

    private static void EnsureSpecPresent(Assignment assignment, string type, SpecBundle bundle)
    {
        var present = type switch
        {
            "code-test" => bundle.Code is not null,
            "image-test" => bundle.Image is not null,
            "test" => bundle.Test is not null,
            "math" => bundle.Math is not null,
            "sql-test" => true,
            _ => false
        };
        if (!present)
            throw new InvalidOperationException($"DATA_INTEGRITY_ERROR: assignment {assignment.Id:D} ({type}) is missing its typed spec.");
    }

    private static JsonObject BuildDto(Assignment assignment, string type, SpecBundle bundle, bool includeSensitive, bool isSolved)
    {
        var node = BuildCommon(assignment, type, includeSensitive, isSolved, includeDescription: true);
        AddExecutableFields(node, type, bundle, includeSensitive, includeTests: true);
        if (type is not ("code-test" or "image-test"))
        {
            node["language"] = string.Empty;
            node["allowedLanguages"] = new JsonArray();
            node["starterCode"] = null;
            node["tests"] = null;
            node["testCases"] = null;
            node["testsJson"] = null;
            node["taskConstraints"] = EmptyTaskConstraints();
            node["codeForbiddenCalls"] = new JsonArray();
            node["codeRequiredCalls"] = new JsonArray();
        }
        return node;
    }

    private static JsonObject BuildCommon(Assignment assignment, string type, bool includeSensitive, bool isSolved, bool includeDescription)
    {
        var node = new JsonObject
        {
            ["id"] = assignment.Id,
            ["courseId"] = assignment.CourseId,
            ["title"] = assignment.Title,
            ["type"] = type,
            ["tags"] = assignment.Tags ?? string.Empty,
            ["rating"] = assignment.Rating,
            ["isHidden"] = !assignment.IsVisible,
            ["isAiDraft"] = false,
            ["lifecycleStatus"] = assignment.IsVisible ? "published" : "draft",
            ["isVisible"] = assignment.IsVisible,
            ["sort"] = assignment.Sort,
            ["canEdit"] = includeSensitive,
            ["isSolved"] = isSolved,
            ["solvedByCurrentUser"] = isSolved,
            ["progressStatus"] = isSolved ? "solved" : "not-started",
            ["createdAt"] = assignment.CreatedAt,
            ["updatedAt"] = assignment.UpdatedAt
        };
        if (includeDescription) node["description"] = assignment.Description ?? string.Empty;
        if (includeSensitive)
            node["analyticsSettings"] = JsonSerializer.SerializeToNode(ParseJson(assignment.AnalyticsSettingsJson) ?? AssignmentAnalyticsSettingsService.ToPublicDto(AssignmentAnalyticsSettingsService.Default()), JsonOptions());
        else
            node["analyticsSettings"] = null;
        return node;
    }

    private static void AddExecutableFields(JsonObject node, string type, SpecBundle bundle, bool includeSensitive, bool includeTests)
    {
        if (type is not ("code-test" or "image-test"))
        {
            node["language"] = string.Empty;
            node["allowedLanguages"] = new JsonArray();
            node["starterCode"] = null;
            node["taskConstraints"] = EmptyTaskConstraints();
            node["codeForbiddenCalls"] = new JsonArray();
            node["codeRequiredCalls"] = new JsonArray();
            return;
        }

        var language = type == "code-test" ? bundle.Code!.Language : bundle.Image!.Language;
        var allowedCsv = type == "code-test" ? bundle.Code!.AllowedLanguagesCsv : bundle.Image!.AllowedLanguagesCsv;
        var starter = type == "code-test" ? bundle.Code!.StarterCode : bundle.Image!.StarterCode;
        var forbiddenJson = type == "code-test" ? bundle.Code!.CodeForbiddenCallsJson : bundle.Image!.CodeForbiddenCallsJson;
        var requiredJson = type == "code-test" ? bundle.Code!.CodeRequiredCallsJson : bundle.Image!.CodeRequiredCallsJson;

        node["language"] = language;
        node["allowedLanguages"] = JsonSerializer.SerializeToNode(ParseCsv(allowedCsv, language), JsonOptions());
        node["starterCode"] = starter;
        node["taskConstraints"] = BuildTaskConstraints(type, bundle);
        node["codeForbiddenCalls"] = includeSensitive ? JsonSerializer.SerializeToNode(ParseStringArrayJson(forbiddenJson), JsonOptions()) : new JsonArray();
        node["codeRequiredCalls"] = includeSensitive ? JsonSerializer.SerializeToNode(ParseStringArrayJson(requiredJson), JsonOptions()) : new JsonArray();
        if (includeTests) AddTestsFields(node, type, bundle, includeSensitive);
    }

    private static void AddTestsFields(JsonObject node, string type, SpecBundle bundle, bool includeSensitive)
    {
        if (type is "code-test" or "image-test")
        {
            var testsJson = type == "code-test" ? bundle.Code!.TestsJson : bundle.Image!.TestsJson;
            var tests = includeSensitive ? ParseJsonElement(testsJson) : PublicTestsJson(testsJson);
            node["tests"] = tests.HasValue ? JsonNode.Parse(tests.Value.GetRawText()) : null;
            node["testCases"] = tests.HasValue ? JsonNode.Parse(tests.Value.GetRawText()) : null;
            node["testsJson"] = includeSensitive ? testsJson : null;
            if (type == "image-test")
            {
                node["imageTestReferenceKey"] = includeSensitive ? JsonString(testsJson, "imageTestReferenceKey") : null;
                node["imageTestSimilarityThreshold"] = JsonInt(testsJson, "imageTestSimilarityThreshold", 90);
            }
            return;
        }

        node["tests"] = null;
        node["testCases"] = null;
        node["testsJson"] = null;
    }

    private static JsonObject BuildTaskConstraints(string type, SpecBundle bundle)
    {
        if (type is not ("code-test" or "image-test")) return EmptyTaskConstraints();
        var forbidden = type == "code-test" ? bundle.Code!.CodeForbiddenCallsJson : bundle.Image!.CodeForbiddenCallsJson;
        var required = type == "code-test" ? bundle.Code!.CodeRequiredCallsJson : bundle.Image!.CodeRequiredCallsJson;
        return new JsonObject
        {
            ["kind"] = "assignment",
            ["required"] = JsonSerializer.SerializeToNode(ParseStringArrayJson(required), JsonOptions()),
            ["forbidden"] = JsonSerializer.SerializeToNode(ParseStringArrayJson(forbidden), JsonOptions()),
            ["note"] = "Это правила конкретного задания. Системная политика безопасности платформы в этот список не входит."
        };
    }

    private static JsonObject EmptyTaskConstraints() => new()
    {
        ["kind"] = "assignment",
        ["required"] = new JsonArray(),
        ["forbidden"] = new JsonArray(),
        ["note"] = string.Empty
    };
}
