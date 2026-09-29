using TaskForge.Tasks.Api.Services.Import;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class AssignmentImportPolicyTests
{
    [Theory]
    [InlineData("sql-test", "test")]
    [InlineData("test", "sql-test")]
    [InlineData("SQL-TEST", "math")]
    public void SqlBoundaryTypeChangesAreImmutable(string currentType, string requestedType)
    {
        Assert.True(AssignmentImportPolicy.IsImmutableSqlTypeChange(currentType, requestedType));
    }

    [Theory]
    [InlineData("sql-test", "sql-test")]
    [InlineData("test", "math")]
    [InlineData("code-test", "test")]
    [InlineData("test", null)]
    public void OtherTypeUpdatesAreNotBlockedBySqlPolicy(string currentType, string? requestedType)
    {
        Assert.False(AssignmentImportPolicy.IsImmutableSqlTypeChange(currentType, requestedType));
    }
}
