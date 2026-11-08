using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Testing;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TestAttemptSnapshotRegressionTests
{
    private static TaskSpec Spec(Guid id, string prompt, string answer = "b", int passPercent = 60)
        => new(
            new TestSettings(3, false, passPercent, true, false, true, []),
            [new TestQuestion(id, 0, "single-choice", prompt,
                [new Option("a", "Wrong"), new Option("b", "Right")],
                [answer], [], false, true,
                """{"type":"doc","content":[{"type":"image","attrs":{"src":"/api/files/img","width":240,"monochrome":true}}]}""",
                "rich", "inline", JsonSerializer.Serialize(new { type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = $"{prompt} explanation" } } } } }))]);

    [Fact]
    public void InProgressAttempt_UsesOriginalQuestionsAndScoringEvenAfterImportReplacesIds()
    {
        var originalId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var original = Spec(originalId, "Original question", "b", 75);
        var replaced = Spec(currentId, "New question", "a", 50);
        var attempt = new TaskAttempt { OrderJson = TestAttemptQuestionSnapshot.Create(original, [originalId]) };

        Assert.True(TestAttemptQuestionSnapshot.TryResolve(attempt, replaced, out var resolved, out var order));
        Assert.Equal(originalId, Assert.Single(order));
        Assert.Equal(75, resolved.Settings.PassPercent);
        var question = Assert.Single(resolved.Questions);
        Assert.Equal("Original question", question.Prompt);
        Assert.Contains("Original question explanation", question.ExplanationContentJson!);
        Assert.DoesNotContain("New question explanation", question.ExplanationContentJson!);
        Assert.Contains("\"width\":240", question.PromptContentJson!);
        Assert.True(AssignmentApiTestingService.IsTestCorrect(question,
            new TestAnswer(originalId, "b", [], null)));
        Assert.False(AssignmentApiTestingService.IsTestCorrect(question,
            new TestAnswer(originalId, "a", [], null)));

        var view = JsonSerializer.SerializeToElement(AssignmentApiTestingService.TestStartDto(attempt, replaced),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(75, view.GetProperty("passPercent").GetInt32());
        Assert.Equal(originalId.ToString(), view.GetProperty("questions")[0].GetProperty("id").GetString());
        Assert.Equal("Original question", view.GetProperty("questions")[0].GetProperty("prompt").GetString());
        Assert.False(view.GetProperty("questions")[0].TryGetProperty("correctOptionKeys", out _));
        Assert.False(view.GetProperty("questions")[0].TryGetProperty("explanationContentJson", out _));
    }

    [Fact]
    public void LegacyAttemptWithMissingIds_IsRejectedInsteadOfScoringAnEmptyTest()
    {
        var originalId = Guid.NewGuid();
        var replacement = Spec(Guid.NewGuid(), "Updated question");
        var attempt = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { originalId }) };
        Assert.False(TestAttemptQuestionSnapshot.TryResolve(attempt, replacement, out _, out _));
        Assert.Throws<InvalidOperationException>(() => AssignmentApiTestingService.TestStartDto(attempt, replacement));
    }

    [Fact]
    public void LegacyAttemptWithUnchangedQuestionIds_RemainsReadable()
    {
        var id = Guid.NewGuid();
        var attempt = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { id }) };
        Assert.True(TestAttemptQuestionSnapshot.TryResolve(attempt, Spec(id, "Old test"), out _, out var order));
        Assert.Equal(id, Assert.Single(order));
    }

    [Fact]
    public void ImportUpgradesAnExistingOpenAttemptBeforeReplacingItsQuestions()
    {
        var oldId = Guid.NewGuid();
        var legacy = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { oldId }) };
        var previous = Spec(oldId, "Original", "b", 80);

        Assert.True(TestAttemptQuestionSnapshot.TryUpgradeLegacy(legacy, previous));
        Assert.StartsWith("{", legacy.OrderJson!);
        Assert.True(TestAttemptQuestionSnapshot.TryResolve(legacy,
            Spec(Guid.NewGuid(), "Replacement", "a", 40), out var saved, out var order));
        Assert.Equal(oldId, Assert.Single(order));
        Assert.Equal("Original", Assert.Single(saved.Questions).Prompt);
        Assert.Equal(80, saved.Settings.PassPercent);
        Assert.False(TestAttemptQuestionSnapshot.TryUpgradeLegacy(legacy, previous));
    }

    [Fact]
    public void InvalidSnapshot_IsNeverSilentlyReplacedWithCurrentQuestions()
    {
        var id = Guid.NewGuid();
        var attempt = new TaskAttempt { OrderJson = """{"version":1,"order":[],"spec":{"questions":[]}}""" };
        Assert.False(TestAttemptQuestionSnapshot.TryResolve(attempt, Spec(id, "Live"), out _, out _));
    }

    [Fact]
    public void IdenticalReimportWithoutIds_ReusesOnlyUnambiguousPersistedQuestionIds()
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
    public void AmbiguousReimport_NeverGuessesByArrayPosition()
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
