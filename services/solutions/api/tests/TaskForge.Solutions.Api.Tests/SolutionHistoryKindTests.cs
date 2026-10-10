using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Endpoints;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class SolutionHistoryKindTests
{
    [Fact]
    public void KindFilter_SeparatesSqlAndCodeBeforePagination()
    {
        var code = new SolutionSubmission { Id = Guid.NewGuid(), SqlSpecVersionId = null, Language = "python" };
        var sql = new SolutionSubmission { Id = Guid.NewGuid(), SqlSpecVersionId = Guid.NewGuid(), Language = "sql" };
        var legacySql = new SolutionSubmission { Id = Guid.NewGuid(), SqlSpecVersionId = null, Language = "sql" };
        var profiledSql = new SolutionSubmission { Id = Guid.NewGuid(), SqlEngineProfileId = Guid.NewGuid(), Language = "python" };
        var uppercaseSql = new SolutionSubmission { Id = Guid.NewGuid(), Language = "SQL" };
        var all = new[] { code, sql, legacySql, profiledSql, uppercaseSql }.AsQueryable();

        Assert.Equal(new[] { code.Id }, SolutionsApiEndpoints.FilterSolutionHistoryKind(all, "code").Select(x => x.Id).ToArray());
        Assert.Equal(new[] { sql.Id, legacySql.Id, profiledSql.Id, uppercaseSql.Id }, SolutionsApiEndpoints.FilterSolutionHistoryKind(all, "sql").Select(x => x.Id).ToArray());
        Assert.Equal(5, SolutionsApiEndpoints.FilterSolutionHistoryKind(all, null).Count());
    }

    [Fact]
    public void KindFilter_AfterUserScoping_CannotDeleteAnotherUsersSubmissions()
    {
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var selectedCode = new SolutionSubmission { Id = Guid.NewGuid(), UserId = selected, Language = "python" };
        var selectedSql = new SolutionSubmission { Id = Guid.NewGuid(), UserId = selected, Language = "sql" };
        var otherCode = new SolutionSubmission { Id = Guid.NewGuid(), UserId = other, Language = "python" };
        var otherSql = new SolutionSubmission { Id = Guid.NewGuid(), UserId = other, Language = "sql" };
        var owned = new[] { selectedCode, selectedSql, otherCode, otherSql }
            .AsQueryable().Where(x => x.UserId == selected);

        Assert.Equal(new[] { selectedCode.Id }, SolutionsApiEndpoints.FilterSolutionHistoryKind(owned, "code").Select(x => x.Id).ToArray());
        Assert.Equal(new[] { selectedSql.Id }, SolutionsApiEndpoints.FilterSolutionHistoryKind(owned, "sql").Select(x => x.Id).ToArray());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("CODE", true)]
    [InlineData("Sql", true)]
    [InlineData("images", false)]
    [InlineData("all-code", false)]
    public void KindFilter_RejectsInvalidQueryValues(string? kind, bool valid)
    {
        Assert.Equal(valid, SolutionsApiEndpoints.IsValidSolutionHistoryKind(kind));
    }
}
