using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Services.Testing;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TestingCompatibilityTests
{
    [Fact]
    public void SingleChoice_UsesLegacyScalarWhenListIsExplicitlyEmpty()
    {
        var questionId = Guid.NewGuid();
        var question = new TestQuestion(
            questionId,
            0,
            "single-choice",
            "Pick B",
            [new Option("a", "A"), new Option("b", "B")],
            ["b"],
            [],
            false,
            true);

        var answer = new TestAnswer(questionId, "b", [], null);

        Assert.True(AssignmentApiTestingService.IsTestCorrect(question, answer));
    }

    [Fact]
    public void SingleChoice_NonEmptyListRemainsAuthoritative()
    {
        var questionId = Guid.NewGuid();
        var question = new TestQuestion(
            questionId,
            0,
            "single-choice",
            "Pick B",
            [new Option("a", "A"), new Option("b", "B")],
            ["b"],
            [],
            false,
            true);

        var answer = new TestAnswer(questionId, "a", ["b"], null);

        Assert.True(AssignmentApiTestingService.IsTestCorrect(question, answer));
    }
}
