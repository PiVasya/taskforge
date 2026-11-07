using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Mapping;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Testing;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TestAssignmentSpecTests
{
    [Fact]
    public void CanonicalTestExport_UsesDetachedSpecAndOmitsCodeFields()
    {
        var questionId = Guid.NewGuid();
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(),
            CourseId = Guid.NewGuid(),
            Type = "test",
            Title = "Проверка знаний",
            Language = "csharp",
            AllowedLanguagesCsv = "csharp",
            StarterCode = "Console.WriteLine(1);",
            TestsJson = "{\"testCases\":[{\"input\":\"legacy-code-test\"}]}"
        };
        var spec = new TaskSpec(
            new TestSettings(3, true, 80, false, true, true, [null, 1200]),
            [new TestQuestion(questionId, 0, "single-choice", "2 + 2?", [new Option("a", "3"), new Option("b", "4")], ["b"], [], false, true)]);

        var exported = AssignmentApiSerializationService.ToImportDto(assignment, spec);

        Assert.False(exported.ContainsKey("language"));
        Assert.False(exported.ContainsKey("allowedLanguages"));
        Assert.False(exported.ContainsKey("starterCode"));
        Assert.False(exported.ContainsKey("testCases"));
        Assert.Equal("test", exported["type"]?.GetValue<string>());
        var questions = Assert.IsType<JsonArray>(exported["questions"]);
        Assert.Single(questions);
        Assert.Equal(questionId.ToString(), questions[0]?["id"]?.GetValue<string>());
    }

    [Fact]
    public void RichQuizQuestionAndOptions_RoundTripThroughSpecAndLearnerDtos()
    {
        var content = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Пример"}]}]}""";
        var fraction = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mathInline","attrs":{"kind":"fraction","numerator":"1","denominator":"7"}}]}]}""";
        var question = new TestQuestion(
            Guid.NewGuid(), 0, "single-choice", "Найдите дробь",
            [new Option("a", "1/7", fraction, "rich"), new Option("b", "7", null, "plain")],
            ["a"], [], false, true, content, "rich", "inline");
        var spec = new TaskSpec(new TestSettings(1, false, 60, false, false, true, []), [question]);

        var json = AssignmentApiSerializationService.TaskSpecToJsonObject(spec);
        var restored = AssignmentApiSerializationService.ParseTaskSpec(json);
        var parsed = Assert.Single(restored.Questions);

        Assert.Equal(content, parsed.PromptContentJson);
        Assert.Equal("rich", parsed.PromptEditorMode);
        Assert.Equal("inline", parsed.OptionsLayout);
        Assert.Equal(fraction, parsed.Options[0].ContentJson);
        Assert.Equal("rich", parsed.Options[0].EditorMode);

        var publicDto = JsonSerializer.SerializeToElement(
            AssignmentApiTestingService.TestQuestionPublicDto(parsed, false, Guid.NewGuid()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(content, publicDto.GetProperty("promptContentJson").GetString());
        Assert.Equal("inline", publicDto.GetProperty("optionsLayout").GetString());
        Assert.Equal(fraction, publicDto.GetProperty("options")[0].GetProperty("contentJson").GetString());
        Assert.False(publicDto.TryGetProperty("correctOptionKeys", out _));
        Assert.False(publicDto.GetProperty("options")[0].TryGetProperty("editorMode", out _));

        var review = AssignmentApiTestingService.TestQuestionReviewNode(parsed, null, false);
        Assert.Equal(content, review["promptContentJson"]?.GetValue<string>());
        Assert.Equal("inline", review["optionsLayout"]?.GetValue<string>());
    }

    [Fact]
    public void LegacyTestJson_RemainsReadableDuringTransition()
    {
        var questionId = Guid.NewGuid();
        var assignment = new Assignment
        {
            Type = "test",
            TestsJson = $$"""
            {
              "settings": {
                "maxAttempts": 2,
                "unlimitedAttempts": true,
                "passPercent": 75,
                "shuffleQuestions": false,
                "shuffleAnswers": true,
                "allowReview": true,
                "attemptTimeLimitsSeconds": [null, 600]
              },
              "questions": [
                {
                  "id": "{{questionId}}",
                  "order": 0,
                  "type": "fill",
                  "prompt": "SQL keyword",
                  "options": [],
                  "correctOptionKeys": [],
                  "acceptedAnswers": ["SELECT"],
                  "caseSensitive": false,
                  "trim": true
                }
              ]
            }
            """
        };

        var spec = TestAssignmentSpecService.ReadLegacy(assignment);

        Assert.True(spec.Settings.UnlimitedAttempts);
        Assert.Equal(75, spec.Settings.PassPercent);
        Assert.Single(spec.Questions);
        Assert.Equal(questionId, spec.Questions[0].Id);
        Assert.Equal("SELECT", Assert.Single(spec.Questions[0].AcceptedAnswers));
    }

    [Fact]
    public void OrdinaryTestDto_DoesNotExposeCodeJudgeState()
    {
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(),
            CourseId = Guid.NewGuid(),
            Type = "test",
            Title = "Тест",
            Language = "csharp",
            AllowedLanguagesCsv = "csharp,cpp",
            StarterCode = "legacy",
            TestsJson = "{\"publicTests\":[{\"input\":\"secret\"}]}",
            CodeForbiddenCallsJson = "[\"exec\"]",
            CodeRequiredCallsJson = "[\"main\"]"
        };

        var dto = AssignmentApiMappingService.ToDto(assignment, includeSensitive: true);
        var node = JsonSerializer.SerializeToNode(dto, AssignmentApiSerializationService.JsonOptions())!.AsObject();

        Assert.Equal(string.Empty, node["language"]?.GetValue<string>());
        Assert.Equal(0, node["allowedLanguages"]?.AsArray().Count);
        Assert.Null(node["starterCode"]);
        Assert.Null(node["tests"]);
        Assert.Null(node["testCases"]);
        Assert.Null(node["testsJson"]);
        Assert.Equal(0, node["codeForbiddenCalls"]?.AsArray().Count);
        Assert.Equal(0, node["codeRequiredCalls"]?.AsArray().Count);
        Assert.Null(node["imageTestSimilarityThreshold"]);
    }

}
