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

public sealed class SqlPublicationPolicyTests
{
    private static readonly Guid Draft = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void InitialRevision_AutoPublishesOnlyWhenEveryEnabledTargetIsReady()
    {
        var ready = new[]
        {
            new SqlPublicationTargetState(true, "valid", "valid", true),
            new SqlPublicationTargetState(true, "valid", "valid", true),
            new SqlPublicationTargetState(false, "pending", "pending", false)
        };

        Assert.True(SqlPublicationPolicy.CanAutoPublishInitialRevision(null, Draft, Draft, ready));
    }

    [Theory]
    [InlineData("pending", "valid", true)]
    [InlineData("valid", "pending", true)]
    [InlineData("valid", "valid", false)]
    public void InitialRevision_DoesNotPublishUntilValidationIsComplete(string expected, string dataset, bool hasContent)
    {
        var targets = new[] { new SqlPublicationTargetState(true, expected, dataset, hasContent) };

        Assert.False(SqlPublicationPolicy.CanAutoPublishInitialRevision(null, Draft, Draft, targets));
    }

    [Fact]
    public void LaterDraft_NeverAutoPublishesOverExistingPublishedRevision()
    {
        var published = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var targets = new[] { new SqlPublicationTargetState(true, "valid", "valid", true) };

        Assert.False(SqlPublicationPolicy.CanAutoPublishInitialRevision(published, Draft, Draft, targets));
    }

    [Fact]
    public void AutoPublication_RequiresCurrentDraftAndAtLeastOneEnabledEngine()
    {
        var otherDraft = Guid.Parse("33333333-3333-3333-3333-333333333333");
        Assert.False(SqlPublicationPolicy.CanAutoPublishInitialRevision(null, otherDraft, Draft,
            new[] { new SqlPublicationTargetState(true, "valid", "valid", true) }));
        Assert.False(SqlPublicationPolicy.CanAutoPublishInitialRevision(null, Draft, Draft,
            new[] { new SqlPublicationTargetState(false, "valid", "valid", true) }));
    }
}
