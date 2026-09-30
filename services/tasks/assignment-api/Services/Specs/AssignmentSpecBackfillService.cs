using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Specs;

internal static class AssignmentSpecBackfillService
{
    private const int BatchSize = 500;

    internal static async Task BackfillAsync(TasksDbContext db, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(6072612973691553091);", ct);

        var createdCode = 0;
        var createdImage = 0;
        var createdMath = 0;
        var createdTest = 0;
        var canonicalizedTypes = 0;

        var codeTypes = new[] { "code-test", "code", "code_test", "codetest", "programming", "programming-test" };
        var imageTypes = new[] { "image-test", "image", "image_test", "imagetest", "drawing", "drawing-test" };
        var mathTypes = new[] { "math", "math-test", "math_task", "math-task" };
        var testTypes = new[] { "test", "quiz", "task-test", "multiple-choice" };
        var knownTypes = codeTypes.Concat(imageTypes).Concat(mathTypes).Concat(testTypes).Distinct().ToArray();

        var aliases = await db.Assignments
            .Where(a => knownTypes.Contains(a.Type.ToLower()))
            .ToListAsync(ct);
        foreach (var assignment in aliases)
        {
            if (!TryNormalizeAssignmentType(assignment.Type, out var normalizedType)) continue;
            if (string.Equals(assignment.Type, normalizedType, StringComparison.Ordinal)) continue;
            assignment.Type = normalizedType;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
            canonicalizedTypes++;
        }
        if (canonicalizedTypes > 0)
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        while (true)
        {
            var rows = await db.Assignments
                .Where(a =>
                    (codeTypes.Contains(a.Type.ToLower()) && !db.CodeAssignmentSpecs.Any(s => s.AssignmentId == a.Id))
                    || (imageTypes.Contains(a.Type.ToLower()) && !db.ImageAssignmentSpecs.Any(s => s.AssignmentId == a.Id))
                    || (mathTypes.Contains(a.Type.ToLower()) && !db.MathAssignmentSpecs.Any(s => s.AssignmentId == a.Id))
                    || (testTypes.Contains(a.Type.ToLower()) && !db.TestAssignmentSpecs.Any(s => s.AssignmentId == a.Id)))
                .OrderBy(a => a.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (rows.Count == 0) break;

            foreach (var assignment in rows)
            {
                var normalizedType = NormalizeAssignmentType(assignment.Type);
                switch (normalizedType)
                {
                    case "code-test":
                        db.CodeAssignmentSpecs.Add(new CodeAssignmentSpec
                        {
                            AssignmentId = assignment.Id,
                            Language = NormalizeLanguage(assignment.Language) ?? "csharp",
                            AllowedLanguagesCsv = assignment.AllowedLanguagesCsv,
                            StarterCode = assignment.StarterCode,
                            TestsJson = ValidJsonOrThrow(assignment.TestsJson, "[]", assignment.Id, "code-test"),
                            CodeForbiddenCallsJson = ValidOptionalJsonOrThrow(assignment.CodeForbiddenCallsJson, assignment.Id, "code-test", "CodeForbiddenCallsJson"),
                            CodeRequiredCallsJson = ValidOptionalJsonOrThrow(assignment.CodeRequiredCallsJson, assignment.Id, "code-test", "CodeRequiredCallsJson")
                        });
                        createdCode++;
                        break;

                    case "image-test":
                        db.ImageAssignmentSpecs.Add(new ImageAssignmentSpec
                        {
                            AssignmentId = assignment.Id,
                            Language = NormalizeLanguage(assignment.Language) ?? "python",
                            AllowedLanguagesCsv = assignment.AllowedLanguagesCsv,
                            StarterCode = assignment.StarterCode,
                            TestsJson = ValidJsonOrThrow(assignment.TestsJson, "{}", assignment.Id, "image-test"),
                            CodeForbiddenCallsJson = ValidOptionalJsonOrThrow(assignment.CodeForbiddenCallsJson, assignment.Id, "image-test", "CodeForbiddenCallsJson"),
                            CodeRequiredCallsJson = ValidOptionalJsonOrThrow(assignment.CodeRequiredCallsJson, assignment.Id, "image-test", "CodeRequiredCallsJson")
                        });
                        createdImage++;
                        break;

                    case "math":
                    {
                        var root = ParseLegacyObjectOrThrow(assignment.TestsJson, assignment.Id, "math");
                        var canonical = MathSpecToJsonObject(ParseMathSpec(root));
                        db.MathAssignmentSpecs.Add(new MathAssignmentSpec
                        {
                            AssignmentId = assignment.Id,
                            SettingsJson = (canonical["settings"] as JsonObject ?? new JsonObject()).ToJsonString(JsonOptions()),
                            BlocksJson = (canonical["blocks"] as JsonArray ?? new JsonArray()).ToJsonString(JsonOptions())
                        });
                        createdMath++;
                        break;
                    }

                    case "test":
                    {
                        var root = ParseLegacyObjectOrThrow(assignment.TestsJson, assignment.Id, "test");
                        var canonical = TaskSpecToJsonObject(ParseTaskSpec(root));
                        db.TestAssignmentSpecs.Add(new TestAssignmentSpec
                        {
                            AssignmentId = assignment.Id,
                            SettingsJson = (canonical["settings"] as JsonObject ?? new JsonObject()).ToJsonString(JsonOptions()),
                            QuestionsJson = (canonical["questions"] as JsonArray ?? new JsonArray()).ToJsonString(JsonOptions())
                        });
                        createdTest++;
                        break;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await tx.CommitAsync(ct);
        var total = createdCode + createdImage + createdMath + createdTest;
        if (total > 0 || canonicalizedTypes > 0)
        {
            Console.WriteLine($"[ASSIGNMENT-SPECS] BACKFILL DONE total={total} code={createdCode} image={createdImage} math={createdMath} test={createdTest} canonicalizedTypes={canonicalizedTypes}");
        }
    }

    private static string ValidJsonOrThrow(string? json, string fallback, Guid assignmentId, string type)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try
        {
            using var _ = JsonDocument.Parse(json);
            return json;
        }
        catch (Exception ex)
        {
            throw InvalidLegacyJson(assignmentId, type, "TestsJson", ex);
        }
    }

    private static string? ValidOptionalJsonOrThrow(string? json, Guid assignmentId, string type, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var _ = JsonDocument.Parse(json);
            return json;
        }
        catch (Exception ex)
        {
            throw InvalidLegacyJson(assignmentId, type, field, ex);
        }
    }

    private static JsonObject ParseLegacyObjectOrThrow(string? json, Guid assignmentId, string type)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException("legacy payload root is not an object");
        }
        catch (Exception ex)
        {
            throw InvalidLegacyJson(assignmentId, type, "TestsJson", ex);
        }
    }

    private static InvalidOperationException InvalidLegacyJson(Guid assignmentId, string type, string field, Exception inner)
    {
        Console.WriteLine($"[ASSIGNMENT-SPECS] BACKFILL INVALID LEGACY JSON assignment={assignmentId} type={type} field={field}; aborting backfill");
        return new InvalidOperationException(
            $"Assignment spec backfill aborted: invalid legacy JSON assignment={assignmentId} type={type} field={field}.",
            inner);
    }
}
