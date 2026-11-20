using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Testing;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TestAttemptCurrentVersionTests
{
    private const string Image = """{"type":"doc","content":[{"type":"image","attrs":{"src":"/api/files/new.png"}}]}""";

    private static TaskSpec Spec(Guid id, string text, string correct = "b", int pass = 60) => new(
        new TestSettings(3, false, pass, true, false, true, []),
        [new TestQuestion(id, 0, "single-choice", text,
            [new Option("a", "A"), new Option("b", "B")], [correct], [], false, true,
            Image, "rich", "inline")]);

    [Fact]
    public void NewAttemptsStoreOnlyIds_NoQuestionOrSecretIsSaved()
    {
        var id = Guid.NewGuid();
        var stored = TestAttemptQuestionOrder.Create([id]);
        Assert.StartsWith("[", stored);
        Assert.DoesNotContain("spec", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correctOptionKeys", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(id, Assert.Single(JsonSerializer.Deserialize<List<Guid>>(stored)!));
    }

    [Fact]
    public void HistoricSnapshotIsIgnoredEvenIfItHasAnOldAnswerKeyAndOldPicture()
    {
        var id = Guid.NewGuid();
        var oldTextOnly = Spec(id, "OLD QUESTION", "a", 90);
        var old = oldTextOnly with { Questions = [oldTextOnly.Questions[0] with { PromptContentJson = null }] };
        var live = Spec(id, "NEW QUESTION", "b", 45);
        var legacy = new JsonObject
        {
            ["version"] = 1,
            ["order"] = JsonSerializer.SerializeToNode(new[] { id }),
            ["spec"] = AssignmentApiSerializationService.TaskSpecToJsonObject(old)
        };
        var attempt = new TaskAttempt { OrderJson = legacy.ToJsonString() };
        var displayed = JsonSerializer.SerializeToElement(
            AssignmentApiTestingService.TestStartDto(attempt, live),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var question = displayed.GetProperty("questions")[0];
        Assert.Equal("NEW QUESTION", question.GetProperty("prompt").GetString());
        Assert.Equal(45, displayed.GetProperty("passPercent").GetInt32());
        Assert.Equal(Image, question.GetProperty("promptContentJson").GetString());
        Assert.False(question.TryGetProperty("correctOptionKeys", out _));
        Assert.False(question.TryGetProperty("explanationContentJson", out _));
        Assert.True(AssignmentApiTestingService.IsTestCorrect(live.Questions[0], new TestAnswer(id, "b", [], null)));
        Assert.False(AssignmentApiTestingService.IsTestCorrect(live.Questions[0], new TestAnswer(id, "a", [], null)));
        Assert.True(TestAttemptQuestionOrder.Refresh(attempt, live));
        Assert.StartsWith("[", attempt.OrderJson!);
        Assert.DoesNotContain("OLD", attempt.OrderJson!);
        Assert.False(TestAttemptQuestionOrder.Refresh(attempt, live));
    }

    [Fact]
    public void PublicStartIncludesLatestRevisionWithoutExposingAnswerKeys()
    {
        var id = Guid.NewGuid();
        var attempt = new TaskAttempt { OrderJson = TestAttemptQuestionOrder.Create([id]) };
        var response = JsonSerializer.SerializeToElement(
            AssignmentApiTestingService.TestStartDto(attempt, Spec(id, "Latest"), revision: "revision-42"));
        Assert.Equal("revision-42", response.GetProperty("testRevision").GetString());
        Assert.False(response.GetProperty("questions")[0].TryGetProperty("correctOptionKeys", out _));
    }

    [Fact]
    public void ReplacementIdsDoNotBlockAnInProgressAttempt()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var attempt = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { oldId }) };
        var latest = Spec(newId, "Replacement");
        Assert.Equal(newId, Assert.Single(TestAttemptQuestionOrder.Resolve(attempt, latest)));
        Assert.True(TestAttemptQuestionOrder.Refresh(attempt, latest));
        Assert.Equal(newId, Assert.Single(JsonSerializer.Deserialize<Guid[]>(attempt.OrderJson!)!));
    }

    [Fact]
    public void NonShuffledAttemptAlwaysFollowsCurrentQuestionOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var old = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { b, a }) };
        var spec = new TaskSpec(
            Spec(a, "A").Settings with { ShuffleQuestions = false },
            [Spec(a, "A").Questions[0], Spec(b, "B").Questions[0]]);
        Assert.Equal(new[] { a, b }, TestAttemptQuestionOrder.Resolve(old, spec));
    }

    [Fact]
    public void ShuffledAttemptKeepsSurvivorsAndIncludesNewQuestions()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var removed = Guid.NewGuid();
        var settings = new TestSettings(2, false, 70, true, false, true, []);
        var spec = new TaskSpec(settings, [Spec(a, "A").Questions[0], Spec(b, "B").Questions[0], Spec(c, "C").Questions[0]]);
        var attempt = new TaskAttempt { OrderJson = JsonSerializer.Serialize(new[] { removed, b, a, b }) };
        var ids = TestAttemptQuestionOrder.Resolve(attempt, spec);
        Assert.Equal(new[] { b, a, c }, ids);
        Assert.True(TestAttemptQuestionOrder.Refresh(attempt, spec));
        Assert.Equal(ids, TestAttemptQuestionOrder.Resolve(attempt, spec));
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("{\"version\":1,\"order\":[],\"spec\":{\"questions\":[]}}")]
    [InlineData("{}")]
    public void DamagedOrEmptyLegacyDataFallsBackToCurrentQuestions(string oldData)
    {
        var currentId = Guid.NewGuid();
        var attempt = new TaskAttempt { OrderJson = oldData };
        Assert.Equal(currentId, Assert.Single(TestAttemptQuestionOrder.Resolve(attempt, Spec(currentId, "New"))));
    }

    [Fact]
    public void EditingLiveTestResetsAttemptClockWithoutChangingAttemptNumber()
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddMinutes(-15);
        var attempt = new TaskAttempt { StartedAt = start, UpdatedAt = start, AttemptNumber = 2, TimeLimitSeconds = 60 };
        var latest = Spec(id, "Updated") with
        {
            Settings = new TestSettings(3, false, 50, false, false, true, [null, 180])
        };
        var changedRevision = DateTimeOffset.UtcNow.ToUniversalTime().Ticks.ToString();
        Assert.True(AssignmentApiTestingService.RefreshTimeLimit(attempt, latest, false, changedRevision));
        Assert.Equal(180, attempt.TimeLimitSeconds);
        Assert.True(attempt.StartedAt > start);
        Assert.Equal(2, attempt.AttemptNumber);
        Assert.False(AssignmentApiTestingService.RefreshTimeLimit(attempt, latest, false, changedRevision));
    }

    [Fact]
    public void HistoricReviewUsesOnlyCurrentQuestionAndCurrentAnswerKey()
    {
        var id = Guid.NewGuid();
        var attempt = new TaskAttempt
        {
            OrderJson = JsonSerializer.Serialize(new[] { id }),
            AnswersJson = JsonSerializer.Serialize(new[] { new TestAnswer(id, "a", ["a"], null) }),
            ReviewJson = """{"questions":[{"prompt":"OLD TEST","correctOptionKeys":["a"]}]}""",
            ScorePercent = 100
        };
        var latest = Spec(id, "NEW TEST", "b");
        var view = AssignmentApiTestingService.CurrentTestReview(attempt, latest);
        var question = Assert.IsType<JsonObject>(Assert.Single(view));
        Assert.Equal("NEW TEST", question["prompt"]?.GetValue<string>());
        Assert.False(question["isCorrect"]!.GetValue<bool>());
        Assert.Equal("b", question["correctOptionKeys"]?[0]?.GetValue<string>());
        Assert.Equal(100, attempt.ScorePercent); // Original historical score is untouched.
    }

    [Fact]
    public void TasksEfModelStoresDefinitionsButDoesNotOwnAttempts()
    {
        // Check EF's runtime model, not a source-text string or deleted cleanup SQL.
        // Attempts for both test and math are persisted by Solutions API.
        using var db = new TasksDbContext(new DbContextOptionsBuilder<TasksDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not-connected;Username=unused").Options);
        Assert.Equal("Assignments", db.Model.FindEntityType(typeof(Assignment))?.GetTableName());
        Assert.Null(db.Model.FindEntityType(typeof(TaskAttempt)));
        Assert.DoesNotContain(db.Model.GetEntityTypes(), entity => entity.GetTableName() == "TaskAttempts");
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
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var original = new JsonArray(
            AssignmentApiSerializationService.TaskSpecToJsonObject(Spec(a, "Repeated"))["questions"]![0]!.DeepClone(),
            AssignmentApiSerializationService.TaskSpecToJsonObject(Spec(b, "Repeated"))["questions"]![0]!.DeepClone());
        var incoming = new JsonArray(original[0]!.DeepClone());
        incoming[0]!["id"] = null;
        AssignmentApiSerializationService.RestoreMatchingQuestionIds(original, incoming);
        Assert.Null(incoming[0]!["id"]);
    }
}
