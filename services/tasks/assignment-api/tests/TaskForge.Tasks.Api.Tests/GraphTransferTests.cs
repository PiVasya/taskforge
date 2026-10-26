using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskForge.Shared.Import;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Endpoints;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Transfer;
using Xunit;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;

namespace TaskForge.Tasks.Api.Tests;

public sealed class GraphTransferTests
{
    private static TasksDbContext QueryContext() => new(new DbContextOptionsBuilder<TasksDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_used;Username=test;Password=test;Timeout=1").Options);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void MetadataAssignmentProjectionNeverSelectsLegacyPayloads(bool content, bool checks)
    {
        using var db = QueryContext();
        var sql = AssignmentApiEndpoints.ExportAssignmentQuery(db, [Guid.NewGuid()],
            new GraphExportOptions(IncludeContent: content, IncludeChecks: checks)).ToQueryString();
        Assert.DoesNotContain("\"Description\"", sql);
        Assert.DoesNotContain("\"TestsJson\"", sql);
        Assert.DoesNotContain("\"StarterCode\"", sql);
        Assert.DoesNotContain("\"AnalyticsSettingsJson\"", sql);
        Assert.Contains("\"Id\"", sql);
    }

    [Fact]
    public void ContentProjectionIncludesDescriptionButNotLegacyChecks()
    {
        using var db = QueryContext();
        var sql = AssignmentApiEndpoints.ExportAssignmentQuery(db, [Guid.NewGuid()], new GraphExportOptions()).ToQueryString();
        Assert.Contains("\"Description\"", sql);
        Assert.DoesNotContain("\"TestsJson\"", sql);
        Assert.DoesNotContain("\"StarterCode\"", sql);
    }

    [Fact]
    public void StatusProjectionNeverSelectsRetainedInputOrArtifactBytes()
    {
        using var db = QueryContext();
        var sql = db.TaskGraphTransfers.Select(AssignmentApiEndpoints.TransferStatusProjection).ToQueryString();
        Assert.DoesNotContain("\"PayloadJson\"", sql);
        Assert.DoesNotContain("TaskGraphTransferParts", sql);
        var job = new TaskGraphTransfer { PayloadJson = "DO_NOT_RETURN_TEST_ANSWERS" };
        var status = AssignmentApiEndpoints.TransferStatusProjection.Compile()(job);
        Assert.DoesNotContain("DO_NOT_RETURN_TEST_ANSWERS", JsonSerializer.Serialize(status));
    }

    [Fact]
    public async System.Threading.Tasks.Task MetadataExportDoesNotOpenTheDatabaseOrReadTypedSpecs()
    {
        using var db = QueryContext();
        var rows = new[] { "code-test", "image-test", "test", "math", "sql-test" }
            .Select(type => new Assignment { Id = Guid.NewGuid(), Type = type, Title = "Caption" }).ToArray();
        // The deliberately unreachable connection makes any accidental spec query fail.
        var result = await AssignmentTypedExportService.LoadImportDtosAsync(db, rows,
            options: new GraphExportOptions(IncludeContent: false, IncludeChecks: false));
        Assert.Equal(rows.Length, result.Count);
        foreach (var dto in result.Values)
        {
            Assert.False(dto.ContainsKey("testCases"));
            Assert.False(dto.ContainsKey("starterCode"));
            Assert.False(dto.ContainsKey("questions"));
            Assert.False(dto.ContainsKey("blocks"));
        }
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("export", "{\"includeContent\":false,\"includeChecks\":false}", "metadata")]
    [InlineData("snapshot", "{\"includeContent\":false,\"includeChecks\":false}", "metadata")]
    [InlineData("export", "{}", "content")]
    [InlineData("export", "{\"includeContent\":false,\"includeChecks\":true}", "content")]
    [InlineData("map", "{}", "metadata")]
    [InlineData("import", "{\"graph\":{\"format\":\"taskforge-task-graph\",\"schemaVersion\":5,\"scopes\":[\"ids\",\"layout\"],\"tasks\":[]}}", "metadata")]
    [InlineData("preview", "{\"graph\":{\"format\":\"taskforge-task-graph\",\"schemaVersion\":5,\"scopes\":[\"content\"],\"tasks\":[]}}", "content")]
    [InlineData("import", "{\"graph\":{\"format\":\"taskforge-task-graph\",\"schemaVersion\":5,\"scopes\":[\"content\"],\"tasks\":[]},\"options\":{\"updateContent\":false}}", "metadata")]
    public void TransferLaneHonorsEffectiveScopes(string kind, string payload, string expected)
        => Assert.Equal(expected, AssignmentApiEndpoints.TransferLane(kind, ImportJsonIdentity.Read(payload)));

    [Fact]
    public void ProgressNeverReachesCompletionBeforePersistedArtifactAndKeepsCounters()
    {
        var progress = new GraphTransferProgress(Guid.NewGuid(), "export", NullLogger.Instance);
        progress.Report("assignments", 70, 500, 511, "assignment-id");
        progress.Report("result.write", 95, bytes: 4096);
        progress.Report("result.write", 100);
        progress.Report("late-phase", 1);
        var result = progress.Read();
        Assert.Equal(99, result.Percent);
        Assert.Equal(500, result.CompletedItems); Assert.Equal(511, result.TotalItems);
        Assert.Equal(4096L, result.BytesWritten); Assert.Equal("assignment-id", result.CurrentItem);
    }

    [Fact]
    public void ProgressUpdatesAreThreadSafeAndOnlyAttachedToItsContext()
    {
        var first = new DefaultHttpContext(); var second = new DefaultHttpContext();
        var progress = new GraphTransferProgress(Guid.NewGuid(), "import", NullLogger.Instance);
        progress.Attach(first);
        Parallel.For(0, 1000, n => progress.Report("assignments", n % 100, n, 1000));
        Assert.Same(progress, GraphTransferProgress.Get(first)); Assert.Null(GraphTransferProgress.Get(second));
        Assert.Equal(99, progress.Read().Percent);
    }

    [Theory]
    [InlineData("{\"blocked\":false,\"accountStatus\":\"active\"}", true)]
    [InlineData("{\"blocked\":true,\"accountStatus\":\"active\"}", false)]
    [InlineData("{\"blocked\":false,\"accountStatus\":\"suspended\"}", false)]
    [InlineData("{\"blocked\":false,\"accountStatus\":\"active\",\"deletedAtUtc\":\"2026-10-26\"}", false)]
    [InlineData("{\"blocked\":false,\"accountStatus\":\"active\",\"mergedIntoUserId\":\"other\"}", false)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    public void WorkerActorFailsClosedForInactiveOrIncompleteIdentity(string json, bool active)
        => Assert.Equal(active, GraphTransferActor.IsActive(ImportJsonIdentity.Read(json)));

    [Fact]
    public void OrdinaryHttpClaimsAndHeadersCannotForgeWorkerCapability()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Internal-Key"] = "client-supplied";
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "GraphTransferWorker"));
        http.Items["GraphTransferActor"] = http.User;
        Assert.Null(GraphTransferActor.Get(http));
    }

    [Fact]
    public void ErrorClassificationIsSafeAndPreservesActionableCategories()
    {
        Assert.Equal(("TRANSFER_INVALID_PAYLOAD", 400), GraphTransferWorker.ClassifyError(new JsonException("secret test")));
        Assert.Equal(("TRANSFER_DEPENDENCY_UNAVAILABLE", 503), GraphTransferWorker.ClassifyError(new HttpRequestException("secret URL")));
        Assert.Equal(("TRANSFER_RESULT_TOO_LARGE", 413), GraphTransferWorker.ClassifyError(new InvalidOperationException("TRANSFER_RESULT_TOO_LARGE")));
        Assert.Equal(("TRANSFER_EXECUTION_FAILED", 500), GraphTransferWorker.ClassifyError(new Exception("password should never reach status")));
    }

    [Fact]
    public void ResultPartsUseCompositeIdentityAndCascadeRetention()
    {
        using var db = QueryContext();
        var part = db.Model.FindEntityType(typeof(TaskGraphTransferPart))!;
        Assert.Equal(new[] { "TransferId", "Sequence" }, part.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(part.GetForeignKeys()).DeleteBehavior);
    }
}
