using TaskForge.Tasks.Api.Services.Serialization;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TaskGraphContractTests
{
    [Fact]
    public void CanonicalGraphContract_StaysBackwardCompatible()
    {
        Assert.Equal(5, AssignmentTaskGraphJsonService.SchemaVersion);
        Assert.Equal(4, AssignmentTaskGraphJsonService.PreviousSchemaVersion);
        Assert.Equal(3, AssignmentTaskGraphJsonService.LegacySchemaVersion);
        Assert.Equal(5000, AssignmentTaskGraphJsonService.MaxTasks);
    }
}
