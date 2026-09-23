using System.Text.Json;
using TaskForge.Sql;
using TaskForge.Tasks.Api.Services.Sql;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class SqlTaskGraphImportTests
{
    [Fact]
    public void NewImportedDatasetVersion_StartsAtVersionOne()
    {
        var input = new SqlGraphDataset(
            "dataset-test",
            "Dataset test",
            null,
            new SqlDefinition(Array.Empty<SqlTable>(), Array.Empty<string>()),
            new Dictionary<string, List<Dictionary<string, JsonElement>>>(),
            new Dictionary<string, SqlEngineMapping>());

        var version = SqlTaskGraphService.CreateImportedDatasetVersion(input);

        Assert.NotEqual(Guid.Empty, version.Id);
        Assert.Equal(1, version.Version);
        Assert.Equal(SqlWire.Version, version.DefinitionSchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(version.DefinitionJson));
        Assert.False(string.IsNullOrWhiteSpace(version.SeedJson));
    }
}
