using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Shared.Import;
using TaskForge.Tasks.Api.Endpoints;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TaskGraphJournalTests
{
    [Fact]
    public void FingerprintIgnoresObjectPropertyOrderAndFormatting()
    {
        var first = ImportJsonIdentity.Read("{\"b\":[1,2],\"a\":{\"z\":3,\"x\":4}}");
        var second = ImportJsonIdentity.Read(" { \"a\":{\"x\":4,\"z\":3},\"b\":[1,2] } ");
        Assert.Equal(ImportJsonIdentity.Hash(first), ImportJsonIdentity.Hash(second));
    }

    [Theory]
    [InlineData("[1,2]", "[2,1]")]
    [InlineData("{\"expectedOutput\":\"x\\n\"}", "{\"expectedOutput\":\"x\"}")]
    [InlineData("{\"input\":\"\"}", "{\"input\":\" \"}")]
    [InlineData("{\"allowDeletes\":false}", "{\"allowDeletes\":true}")]
    public void FingerprintPreservesOrderTestBytesAndWriteOptions(string first, string second)
        => Assert.NotEqual(ImportJsonIdentity.Hash(ImportJsonIdentity.Read(first)), ImportJsonIdentity.Hash(ImportJsonIdentity.Read(second)));

    [Fact]
    public void ReservedIdentitiesSurviveSerializationAndSecondReservation()
    {
        var raw = ImportJsonIdentity.Read("{\"schemaVersion\":5,\"scopes\":[\"content\"],\"courses\":[{\"key\":\"sub\"}],\"tasks\":[{\"key\":\"a\"}]}");
        var first = ImportJsonIdentity.ReserveMissingIds(raw);
        var second = ImportJsonIdentity.ReserveMissingIds(ImportJsonIdentity.Read(first.GetRawText()));
        Assert.Equal(ImportJsonIdentity.Hash(first), ImportJsonIdentity.Hash(second));
        Assert.True(Guid.TryParse(first.GetProperty("courses")[0].GetProperty("id").GetString(), out _));
        Assert.True(Guid.TryParse(first.GetProperty("tasks")[0].GetProperty("id").GetString(), out _));
        Assert.Contains(first.GetProperty("scopes").EnumerateArray(), x => x.GetString() == "ids");
        Assert.False(raw.GetProperty("tasks")[0].TryGetProperty("id", out _));
    }

    [Theory]
    [InlineData("[{\"title\":\"A\"}]")]
    [InlineData("{\"assignments\":[{\"title\":\"A\"}]}")]
    [InlineData("{\"items\":[{\"title\":\"A\"}]}")]
    [InlineData("{\"assignment\":{\"title\":\"A\"}}")]
    [InlineData("{\"title\":\"A\"}")]
    public void LegacyWrappersAlsoGetStableReservations(string input)
    {
        var first = ImportJsonIdentity.ReserveMissingIds(ImportJsonIdentity.Read(input));
        Assert.Contains("\"id\"", first.GetRawText());
        Assert.Equal(ImportJsonIdentity.Hash(first), ImportJsonIdentity.Hash(ImportJsonIdentity.ReserveMissingIds(first)));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("\"invalid\"")]
    public void InvalidScalarInputIsLeftForValidator(string json)
        => Assert.Equal(ImportJsonIdentity.Hash(ImportJsonIdentity.Read(json)), ImportJsonIdentity.Hash(ImportJsonIdentity.ReserveMissingIds(ImportJsonIdentity.Read(json))));

    [Fact]
    public void MalformedExplicitIdentityIsNotRepaired()
    {
        var raw = ImportJsonIdentity.Read("{\"schemaVersion\":5,\"tasks\":[{\"key\":\"a\",\"id\":\"invalid\"}]}");
        Assert.Equal("invalid", ImportJsonIdentity.ReserveMissingIds(raw).GetProperty("tasks")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void AuditOptionsCannotCarrySecretsOrUnknownSettings()
    {
        var options = AssignmentApiEndpoints.SafeImportOptions(ImportJsonIdentity.Read("{\"updateChecks\":false,\"allowDeletes\":true,\"password\":\"SECRET\",\"connectionMode\":\"merge\",\"expectedMapVersion\":7}"));
        Assert.False(options["updateChecks"]!.GetValue<bool>());
        Assert.True(options["allowDeletes"]!.GetValue<bool>());
        Assert.Equal(7, options["expectedMapVersion"]!.GetValue<int>());
        Assert.DoesNotContain("SECRET", options.ToJsonString());
        Assert.DoesNotContain("password", options.ToJsonString());
    }

    [Fact]
    public void PerEntityAuditContainsFieldNamesAndHashesButNeverContents()
    {
        var first = JsonNode.Parse("{\"testCases\":[{\"input\":\"SECRET\",\"expectedOutput\":\"A\\n\"}]}")!.AsObject();
        var second = JsonNode.Parse("{\"testCases\":[{\"input\":\"SECRET\",\"expectedOutput\":\"B\\n\"}]}")!.AsObject();
        var audit = AssignmentApiEndpoints.ImportEntityChange(Guid.NewGuid(), first, second);
        Assert.Equal("updated", audit["action"]!.GetValue<string>());
        Assert.Equal("testCases", audit["changedFields"]![0]!.GetValue<string>());
        Assert.NotEqual(audit["beforeSha256"]!.ToString(), audit["afterSha256"]!.ToString());
        Assert.DoesNotContain("SECRET", audit.ToJsonString());
    }

    [Fact]
    public void NoOpCreationAndDeletionHaveDistinctOutcomes()
    {
        var id = Guid.NewGuid();
        var value = new JsonObject { ["title"] = "A" };
        Assert.Equal("unchanged", AssignmentApiEndpoints.ImportEntityChange(id, value, value.DeepClone().AsObject())["action"]!.ToString());
        Assert.Equal("created", AssignmentApiEndpoints.ImportEntityChange(id, null, value)["action"]!.ToString());
        Assert.Equal("deleted", AssignmentApiEndpoints.ImportEntityChange(id, value, null)["action"]!.ToString());
    }
    [Fact]
    public void ContentRecoveryBaselineDetectsEditedDeletedAndNewlyOccupiedIdentities()
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        var baseline = new Dictionary<Guid, JsonObject> { [id] = new() { ["title"] = "A" } };
        var hash = AssignmentApiEndpoints.ImportSnapshotHash(baseline);
        Assert.Equal(hash, AssignmentApiEndpoints.ImportSnapshotHash(new Dictionary<Guid, JsonObject> { [id] = new() { ["title"] = "A" } }));
        Assert.NotEqual(hash, AssignmentApiEndpoints.ImportSnapshotHash(new Dictionary<Guid, JsonObject>()));
        baseline[id]["title"] = "Changed";
        Assert.NotEqual(hash, AssignmentApiEndpoints.ImportSnapshotHash(baseline));
        baseline[id]["title"] = "A"; baseline[other] = new() { ["title"] = "New" };
        Assert.NotEqual(hash, AssignmentApiEndpoints.ImportSnapshotHash(baseline));
    }

    [Fact]
    public void MapAuditTracksAddedRemovedAndMovedObjectsWithoutSettingsSecrets()
    {
        var before = AssignmentApiEndpoints.MapImportAuditState(ImportJsonIdentity.Read("{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0}}],\"edges\":[{\"id\":\"e\",\"source\":\"a\",\"target\":\"b\",\"settings\":{\"secret\":\"SECRET\"}}]}"), 2);
        var after = AssignmentApiEndpoints.MapImportAuditState(ImportJsonIdentity.Read("{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":1,\"y\":0}},{\"id\":\"b\"}],\"edges\":[]}"), 3);
        var diff = AssignmentApiEndpoints.MapImportAuditDiff(before, after);
        Assert.Equal("a", diff["nodes"]!["changed"]![0]!.ToString());
        Assert.Equal("b", diff["nodes"]!["added"]![0]!.ToString());
        Assert.Equal("e", diff["edges"]!["removed"]![0]!.ToString());
        Assert.DoesNotContain("SECRET", before.ToJsonString());
    }

}
