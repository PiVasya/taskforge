using TaskForge.AiAgent.Contracts;
using Xunit;

namespace TaskForge.AiAgent.Tests;

public sealed class DraftSpecTests
{
    [Fact]
    public void DraftArtifactContainsBackendCompatibleFields()
    {
        var draft = new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "A",
            Description = "B",
            Language = "cpp",
            ReferenceSolution = "int main(){}",
            PublicTests = [new TestCaseSpec { Input = "", ExpectedOutput = "", IsHidden = false }],
            HiddenTests = [new TestCaseSpec { Input = "1", ExpectedOutput = "1", IsHidden = true }]
        };
        var data = draft.ToArtifactData();
        Assert.Equal("code-test", data["assignmentType"]?.ToString());
        Assert.NotNull(data["publicTests"]);
        Assert.NotNull(data["hiddenTests"]);
    }
}
