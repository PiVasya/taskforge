using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Image;
using TaskForge.Tasks.Api.Services.Testing;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;

namespace TaskForge.Tasks.Api.Services.Specs;

internal sealed record ExecutableAssignmentSpecData(
    string Language,
    string? AllowedLanguagesCsv,
    string? StarterCode,
    string TestsJson,
    string? CodeForbiddenCallsJson,
    string? CodeRequiredCallsJson);

internal static class AssignmentTypeSpecService
{
    internal static async Task<ExecutableAssignmentSpecData> ReadCodeAsync(TasksDbContext db, Assignment assignment, CancellationToken ct = default)
    {
        var row = db.CodeAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.CodeAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is not null) return From(row);
        return string.Equals(NormalizeAssignmentType(assignment.Type), "code-test", StringComparison.Ordinal)
            ? LegacyExecutable(assignment, "csharp", "[]")
            : new ExecutableAssignmentSpecData("csharp", null, null, "[]", null, null);
    }

    internal static async Task<ExecutableAssignmentSpecData> ReadImageAsync(TasksDbContext db, Assignment assignment, CancellationToken ct = default)
    {
        var row = db.ImageAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.ImageAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is not null) return From(row);
        return string.Equals(NormalizeAssignmentType(assignment.Type), "image-test", StringComparison.Ordinal)
            ? LegacyExecutable(assignment, "python", "{}")
            : new ExecutableAssignmentSpecData("python", null, null, "{}", null, null);
    }

    internal static async Task<MathSpec> ReadMathAsync(TasksDbContext db, Assignment assignment, CancellationToken ct = default)
    {
        var row = db.MathAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.MathAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is not null) return ParseMathSpec(ToMathNode(row));

        if (!string.Equals(NormalizeAssignmentType(assignment.Type), "math", StringComparison.Ordinal))
            return ParseMathSpec(new JsonObject());
        return ReadMathSpec(assignment);
    }

    internal static async Task SaveCodeFromRequestAsync(TasksDbContext db, Assignment assignment, AssignmentRequest request, CancellationToken ct = default)
    {
        var current = await ReadCodeAsync(db, assignment, ct);
        var incoming = RawJson(request.TestCases) ?? RawJson(request.Tests) ?? request.TestsJson;
        var language = NormalizeLanguage(request.Language) ?? current.Language;
        var testsJson = incoming is null ? current.TestsJson : NormalizeSpecJsonForStorage(incoming, "code-test") ?? "[]";
        var allowed = request.AllowedLanguages is null ? current.AllowedLanguagesCsv : NormalizeLanguagesCsv(request.AllowedLanguages);
        var starter = request.StarterCode ?? current.StarterCode;
        var forbidden = request.CodeForbiddenCalls is null ? current.CodeForbiddenCallsJson : StringArrayJson(request.CodeForbiddenCalls);
        var required = request.CodeRequiredCalls is null ? current.CodeRequiredCallsJson : StringArrayJson(request.CodeRequiredCalls);

        var row = db.CodeAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.CodeAssignmentSpecs.FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is null)
        {
            row = new CodeAssignmentSpec { AssignmentId = assignment.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.CodeAssignmentSpecs.Add(row);
        }
        row.Language = language;
        row.AllowedLanguagesCsv = allowed;
        row.StarterCode = starter;
        row.TestsJson = string.IsNullOrWhiteSpace(testsJson) ? "[]" : testsJson;
        row.CodeForbiddenCallsJson = forbidden;
        row.CodeRequiredCallsJson = required;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        assignment.Language = language;
        ClearLegacyPayload(assignment, keepLanguage: true);
    }

    internal static async Task SaveImageFromRequestAsync(
        TasksDbContext db,
        Assignment assignment,
        AssignmentRequest request,
        IHttpClientFactory clients,
        IConfiguration cfg,
        CancellationToken ct = default)
    {
        var current = await ReadImageAsync(db, assignment, ct);
        var incoming = RawJson(request.Tests) ?? RawJson(request.TestCases) ?? request.TestsJson;
        var hasRealSpec = HasMeaningfulJsonText(request.TestsJson) || HasMeaningfulJsonElement(request.Tests) || HasMeaningfulJsonElement(request.TestCases);
        var testsJson = current.TestsJson;
        if (hasRealSpec || request.ImageTestReferenceKey is not null || request.ImageTestSimilarityThreshold.HasValue)
        {
            testsJson = (await AssignmentApiImageService.MergeAndMaterializeImageTestPayloadAsync(
                hasRealSpec ? incoming : current.TestsJson,
                request,
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

    internal static async Task<MathSpec> SaveMathFromRequestAsync(TasksDbContext db, Assignment assignment, AssignmentRequest request, CancellationToken ct = default)
    {
        var current = await ReadMathNodeAsync(db, assignment, ct);
        var incoming = RawJson(request.Tests) ?? request.TestsJson;
        var mergedJson = incoming is null
            ? current.ToJsonString(JsonOptions())
            : MergeInteractiveSpecJsonForStorage(current.ToJsonString(JsonOptions()), incoming, "math") ?? "{}";
        var node = JsonNode.Parse(mergedJson) as JsonObject ?? new JsonObject();
        NormalizeIds(node, "blocks");
        return await StoreMathNodeAsync(db, assignment, node, ct);
    }

    internal static async Task<MathSpec> SaveMathPayloadAsync(TasksDbContext db, Assignment assignment, JsonElement payload, CancellationToken ct = default)
    {
        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        NormalizeIds(node, "blocks");
        return await StoreMathNodeAsync(db, assignment, node, ct);
    }

    internal static async Task<ImageAssignmentSpec> SaveImageTestsJsonAsync(TasksDbContext db, Assignment assignment, string testsJson, CancellationToken ct = default)
    {
        var current = await ReadImageAsync(db, assignment, ct);
        var row = db.ImageAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.ImageAssignmentSpecs.FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is null)
        {
            row = new ImageAssignmentSpec
            {
                AssignmentId = assignment.Id,
                Language = current.Language,
                AllowedLanguagesCsv = current.AllowedLanguagesCsv,
                StarterCode = current.StarterCode,
                CodeForbiddenCallsJson = current.CodeForbiddenCallsJson,
                CodeRequiredCallsJson = current.CodeRequiredCallsJson,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ImageAssignmentSpecs.Add(row);
        }
        row.TestsJson = string.IsNullOrWhiteSpace(testsJson) ? "{}" : testsJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        assignment.Language = row.Language;
        ClearLegacyPayload(assignment, keepLanguage: true);
        return row;
    }

    private static async Task<JsonObject> ReadMathNodeAsync(TasksDbContext db, Assignment assignment, CancellationToken ct)
    {
        var row = db.MathAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.MathAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is not null) return ToMathNode(row);
        if (!string.Equals(NormalizeAssignmentType(assignment.Type), "math", StringComparison.Ordinal)) return new JsonObject();
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(assignment.TestsJson) ? "{}" : assignment.TestsJson) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static async Task<MathSpec> StoreMathNodeAsync(TasksDbContext db, Assignment assignment, JsonObject node, CancellationToken ct)
    {
        var parsed = ParseMathSpec(node);
        var canonical = MathSpecToJsonObject(parsed);
        var settingsJson = (canonical["settings"] as JsonObject ?? new JsonObject()).ToJsonString(JsonOptions());
        var blocksJson = (canonical["blocks"] as JsonArray ?? new JsonArray()).ToJsonString(JsonOptions());

        var row = db.MathAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.MathAssignmentSpecs.FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is null)
        {
            row = new MathAssignmentSpec { AssignmentId = assignment.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.MathAssignmentSpecs.Add(row);
        }
        row.SettingsJson = settingsJson;
        row.BlocksJson = blocksJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        assignment.Language = string.Empty;
        ClearLegacyPayload(assignment, keepLanguage: false);
        return parsed;
    }

    private static JsonObject ToMathNode(MathAssignmentSpec row)
    {
        JsonNode? settings;
        JsonNode? blocks;
        try { settings = JsonNode.Parse(string.IsNullOrWhiteSpace(row.SettingsJson) ? "{}" : row.SettingsJson); }
        catch { settings = new JsonObject(); }
        try { blocks = JsonNode.Parse(string.IsNullOrWhiteSpace(row.BlocksJson) ? "[]" : row.BlocksJson); }
        catch { blocks = new JsonArray(); }
        return new JsonObject
        {
            ["settings"] = settings is JsonObject ? settings : new JsonObject(),
            ["blocks"] = blocks is JsonArray ? blocks : new JsonArray()
        };
    }

    private static ExecutableAssignmentSpecData From(CodeAssignmentSpec row)
        => new(row.Language, row.AllowedLanguagesCsv, row.StarterCode, row.TestsJson, row.CodeForbiddenCallsJson, row.CodeRequiredCallsJson);

    private static ExecutableAssignmentSpecData From(ImageAssignmentSpec row)
        => new(row.Language, row.AllowedLanguagesCsv, row.StarterCode, row.TestsJson, row.CodeForbiddenCallsJson, row.CodeRequiredCallsJson);

    private static ExecutableAssignmentSpecData LegacyExecutable(Assignment assignment, string fallbackLanguage, string fallbackTests)
        => new(
            NormalizeLanguage(assignment.Language) ?? fallbackLanguage,
            assignment.AllowedLanguagesCsv,
            assignment.StarterCode,
            string.IsNullOrWhiteSpace(assignment.TestsJson) ? fallbackTests : assignment.TestsJson!,
            assignment.CodeForbiddenCallsJson,
            assignment.CodeRequiredCallsJson);

    private static void ClearLegacyPayload(Assignment assignment, bool keepLanguage)
    {
        if (!keepLanguage) assignment.Language = string.Empty;
        assignment.AllowedLanguagesCsv = null;
        assignment.StarterCode = null;
        assignment.TestsJson = null;
        assignment.CodeForbiddenCallsJson = null;
        assignment.CodeRequiredCallsJson = null;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
