using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Testing;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TestAttemptLatestVersionRegressionTests
{
    private static TaskSpec Spec(Guid id, string prompt, string answer = "b", int passPercent = 60, string imageName = "img")
        => new(
            new TestSettings(3, false, passPercent, false, false, true, []),
            [new TestQuestion(id, 0, "single-choice", prompt,
                [new Option("a", "Wrong"), new Option("b", "Right")],
                [answer], [], false, true,
                JsonSerializer.Serialize(new
                {
                    type = "doc",
                    content = new[] { new { type = "image", attrs = new { src = $"/api/files/{imageName}", width = 240, monochrome = true } } }
                }),
                "rich", "inline", JsonSerializer.Serialize(new
                {
                    type = "doc",
                    content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = $"{prompt} explanation" } } } }
                }))]);

    private static JsonElement Start(TaskAttempt attempt, TaskSpec latest)
        => JsonSerializer.SerializeToElement(
            AssignmentApiTestingService.TestStartDto(attempt, latest),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void InProgressAttemptUsesLatestQuestionsAndScoringEvenAfterImportReplacesIds()
    {
        var originalId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var original = Spec(originalId, "Original question", "b", 75, "old.png");
        var current = Spec(currentId, "New question", "a", 50, "new.png");
        var legacySnapshot = new JsonObject
        {
            ["version"] = 1,
            ["order"] = JsonSerializer.SerializeToNode(new[] { originalId }),
            ["spec"] = AssignmentApiSerializationService.TaskSpecToJsonObject(original)
        };
        var attempt = new TaskAttempt { OrderJson = legacySnapshot.ToJsonString() };

        Assert.Equal(currentId, Assert.Single(TestAttemptQuestionOrder.Resolve(attempt, current)));
        var view = Start(attempt, current);
        var question = view.GetProperty("questions")[0];
        Assert.Equal(50, view.GetProperty("passPercent").GetInt32());
        Assert.Equal(currentId.ToString(), question.GetProperty("id").GetString());
        Assert.Equal("New question", question.GetProperty("prompt").GetString());
        Assert.Equal(current.Questions[0].PromptContentJson, question.GetProperty("promptContentJson").GetString());
        Assert.Contains("new.png", question.GetProperty("promptContentJson").GetString());
        Assert.DoesNotContain("old.png", question.GetProperty("promptContentJson").GetString());
        Assert.False(question.TryGetProperty("correctOptionKeys", out _));
        Assert.False(question.TryGetProperty("explanationContentJson", out _));
        Assert.True(AssignmentApiTestingService.IsTestCorrect(current.Questions[0],
            new TestAnswer(currentId, "a", [], null)));
        Assert.False(AssignmentApiTestingService.IsTestCorrect(current.Questions[0],
            new TestAnswer(currentId, "b", [], null)));

        Assert.True(TestAttemptQuestionOrder.Refresh(attempt, current));
        Assert.StartsWith("[", attempt.OrderJson!);
        Assert.Equal(currentId, Assert.Single(JsonSerializer.Deserialize<Guid[]>(attempt.OrderJson!)!));
        Assert.DoesNotContain("Original question", attempt.OrderJson!);
        Assert.DoesNotContain("correctOptionKeys", attempt.OrderJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyAttemptWithMissingIdsUsesLatestQuestionsInsteadOfScoringEmptyTest()
    {
        var originalId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var current = Spec(currentId, "Updated question", "a");
        var attempt = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { originalId }) };

        Assert.Equal(currentId, Assert.Single(TestAttemptQuestionOrder.Resolve(attempt, current)));
        Assert.Equal(currentId.ToString(), Start(attempt, current).GetProperty("questions")[0].GetProperty("id").GetString());
        Assert.True(AssignmentApiTestingService.IsTestCorrect(current.Questions[0],
            new TestAnswer(currentId, "a", [], null)));
        Assert.True(TestAttemptQuestionOrder.Refresh(attempt, current));
        Assert.Equal(currentId, Assert.Single(JsonSerializer.Deserialize<Guid[]>(attempt.OrderJson!)!));
    }

    [Fact]
    public void LegacyAttemptWithUnchangedQuestionIdsUsesLatestPromptAndPicture()
    {
        var id = Guid.NewGuid();
        var attempt = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { id }) };
        var current = Spec(id, "Current", "a", 65, "current.png");
        var displayed = Start(attempt, current);
        var question = displayed.GetProperty("questions")[0];
        Assert.Equal("Current", question.GetProperty("prompt").GetString());
        Assert.Equal(65, displayed.GetProperty("passPercent").GetInt32());
        Assert.Contains("current.png", question.GetProperty("promptContentJson").GetString());
    }

    [Fact]
    public void ImportReplacesStaleSnapshotWithIdOnlyOrder()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var original = Spec(oldId, "Original", "b", 80);
        var legacySnapshot = new JsonObject
        {
            ["version"] = 1,
            ["order"] = JsonSerializer.SerializeToNode(new[] { oldId }),
            ["spec"] = AssignmentApiSerializationService.TaskSpecToJsonObject(original)
        };
        var attempt = new TaskAttempt { OrderJson = legacySnapshot.ToJsonString() };
        var latest = Spec(newId, "Replacement", "a", 40);

        Assert.True(TestAttemptQuestionOrder.Refresh(attempt, latest));
        Assert.StartsWith("[", attempt.OrderJson!);
        Assert.Equal(newId, Assert.Single(JsonSerializer.Deserialize<Guid[]>(attempt.OrderJson!)!));
        Assert.DoesNotContain("Original", attempt.OrderJson!);
        Assert.False(TestAttemptQuestionOrder.Refresh(attempt, latest));
    }

    [Fact]
    public void InvalidHistoricSnapshotFallsBackToLatestQuestions()
    {
        var id = Guid.NewGuid();
        var attempt = new TaskAttempt { OrderJson = """{"version":1,"order":[],"spec":{"questions":[]}}""" };
        var current = Spec(id, "Live");
        Assert.Equal(id, Assert.Single(TestAttemptQuestionOrder.Resolve(attempt, current)));
        Assert.Equal("Live", Start(attempt, current).GetProperty("questions")[0].GetProperty("prompt").GetString());
        Assert.True(TestAttemptQuestionOrder.Refresh(attempt, current));
        Assert.Equal(id, Assert.Single(JsonSerializer.Deserialize<Guid[]>(attempt.OrderJson!)!));
    }

    [Fact]
    public void IdenticalReimportWithoutIdsReusesOnlyUnambiguousPersistedQuestionIds()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var original = new JsonArray(
            AssignmentApiSerializationService.TaskSpecToJsonObject(Spec(first, "One"))["questions"]![0]!.DeepClone(),
            AssignmentApiSerializationService.TaskSpecToJsonObject(Spec(second, "Two"))["questions"]![0]!.DeepClone());
        var incoming = new JsonArray(original[1]!.DeepClone(), original[0]!.DeepClone());
        incoming[0]!["id"] = null;
        incoming[1]!["id"] = Guid.Empty.ToString();
        incoming[1]!["correctOptionKeys"] = new JsonArray(JsonValue.Create("a"));

        AssignmentApiSerializationService.RestoreMatchingQuestionIds(original, incoming);
        Assert.Equal(second.ToString(), incoming[0]!["id"]!.GetValue<string>());
        Assert.Equal(first.ToString(), incoming[1]!["id"]!.GetValue<string>());
        Assert.Equal("a", incoming[1]!["correctOptionKeys"]![0]!.GetValue<string>());
    }

    [Fact]
    public void AmbiguousReimportNeverGuessesByArrayPosition()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var original = new JsonArray(
            AssignmentApiSerializationService.TaskSpecToJsonObject(Spec(a, "Repeated"))["questions"]![0]!.DeepClone(),
            AssignmentApiSerializationService.TaskSpecToJsonObject(Spec(b, "Repeated"))["questions"]![0]!.DeepClone());
        var incoming = new JsonArray(original[0]!.DeepClone());
        incoming[0]!["id"] = null;

        AssignmentApiSerializationService.RestoreMatchingQuestionIds(original, incoming);
        Assert.Null(incoming[0]!["id"]);
    }
}
