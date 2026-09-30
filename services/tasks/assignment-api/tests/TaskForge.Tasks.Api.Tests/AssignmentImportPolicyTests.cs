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
    public void SqlCompatibilityPolicyOnlyReportsSqlBoundaryChanges(string currentType, string? requestedType)
    {
        Assert.False(AssignmentImportPolicy.IsImmutableSqlTypeChange(currentType, requestedType));
    }

    [Theory]
    [InlineData("code-test", "test")]
    [InlineData("test", "math")]
    [InlineData("math", "image-test")]
    [InlineData("image-test", "code-test")]
    [InlineData("sql-test", "code-test")]
    public void EveryAssignmentKindChangeIsImmutable(string currentType, string requestedType)
    {
        Assert.True(AssignmentImportPolicy.IsImmutableTypeChange(currentType, requestedType));
    }

    [Theory]
    [InlineData("code-test", "code")]
    [InlineData("image-test", "drawing")]
    [InlineData("test", "quiz")]
    [InlineData("math", "math-task")]
    [InlineData("sql-test", "SQL-TEST")]
    public void LegacyAliasesOfSameKindAreNotTypeChanges(string currentType, string requestedType)
    {
        Assert.False(AssignmentImportPolicy.IsImmutableTypeChange(currentType, requestedType));
    }
}
