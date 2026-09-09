using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Domain.Sql;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

namespace TaskForge.Sql.DomainCheck;

internal static class Program
{
    private static int passed;
    private static int failed;
    private const string OfflineConnection = "Host=127.0.0.1;Port=1;Database=sql_domain_model_check;Username=design;Password=unused;Timeout=1";

    private static int Main()
    {
        Test("EF runtime versions match all three API build references", () =>
        {
            var core = typeof(DbContext).Assembly.GetName();
            var relational = typeof(RelationalDatabaseFacadeExtensions).Assembly.GetName();
            var abstractions = Assembly.Load("Microsoft.EntityFrameworkCore.Abstractions").GetName();
            Assert(core.Version == relational.Version && core.Version == abstractions.Version);
            foreach (var assembly in new[] { typeof(TasksDbContext).Assembly, typeof(ExecutionDbContext).Assembly, typeof(SolutionsDbContext).Assembly })
            {
                var reference = assembly.GetReferencedAssemblies().Single(x => x.Name == core.Name);
                Assert(reference.Version == core.Version);
            }
        });

        Test("all three design-time factories build models without web startup", () =>
        {
            using var tasks = new TasksDbContextFactory().CreateDbContext(Array.Empty<string>());
            using var execution = new ExecutionDbContextFactory().CreateDbContext(Array.Empty<string>());
            using var solutions = new SolutionsDbContextFactory().CreateDbContext(Array.Empty<string>());
            Assert(Model(tasks).FindEntityType(typeof(SqlDataset)) is not null);
            Assert(Model(execution).FindEntityType(typeof(ExecutionJob)) is not null);
            Assert(Model(solutions).FindEntityType(typeof(SolutionSubmission)) is not null);
        });

        Test("exactly eight new SQL entity tables", () =>
        {
            using var db = Tasks();
            var tables = Model(db).GetEntityTypes().Where(x => x.ClrType.Namespace == typeof(SqlDataset).Namespace).ToArray();
            Assert(tables.Length == 8);
            Assert(tables.All(x => x.GetTableName()!.StartsWith("Sql", StringComparison.Ordinal)));
        });
        Test("legacy Assignment has no new private navigation or TestsJson changes", () =>
        {
            using var db = Tasks();
            var type = Model(db).FindEntityType(typeof(Assignment))!;
            Assert(type.GetNavigations().Count() == 0);
            Assert(type.FindProperty(nameof(Assignment.TestsJson))!.GetColumnType() == "text");
            Assert(new Assignment().Type == "code-test");
        });
        Test("version numbers unique within each aggregate", () =>
        {
            using var db = Tasks();
            Unique(Model(db), typeof(SqlDatasetVersion), "DatasetId", "Version");
            Unique(Model(db), typeof(SqlAssignmentSpecVersion), "AssignmentId", "Version");
            Unique(Model(db), typeof(SqlEngineProfile), "Key", "Revision");
        });
        Test("publication pointers cannot refer to another assignment", () =>
        {
            using var db = Tasks();
            var pointers = Model(db).FindEntityType(typeof(SqlAssignmentSpec))!.GetForeignKeys()
                .Where(x => x.PrincipalEntityType.ClrType == typeof(SqlAssignmentSpecVersion)).ToArray();
            Assert(pointers.Length == 2);
            Assert(pointers.All(x => x.Properties[0].Name == "AssignmentId" && x.Properties.Count == 2));
            Assert(pointers.All(x => x.PrincipalKey.Properties.Select(y => y.Name).SequenceEqual(new[] { "AssignmentId", "Id" })));
        });
        Test("all SQL history relationships restrict cascaded deletion", () =>
        {
            using var db = Tasks();
            var foreignKeys = Model(db).GetEntityTypes().Where(x => x.ClrType.Namespace == typeof(SqlDataset).Namespace).SelectMany(x => x.GetForeignKeys());
            Assert(foreignKeys.All(x => x.DeleteBehavior == DeleteBehavior.Restrict));
        });
        Test("every immutable mapped property rejects updates", () =>
        {
            using var db = Tasks();
            foreach (var entity in Model(db).GetEntityTypes().Where(x => typeof(ISqlImmutableEntity).IsAssignableFrom(x.ClrType)))
                Assert(entity.GetProperties().All(x => x.GetAfterSaveBehavior() == PropertySaveBehavior.Throw));
        });
        Test("mutable SQL aggregates/receipts have concurrency tokens", () =>
        {
            using var db = Tasks();
            foreach (var entity in Model(db).GetEntityTypes().Where(x => typeof(ISqlMutableEntity).IsAssignableFrom(x.ClrType)))
                Assert(entity.FindProperty("ConcurrencyStamp")!.IsConcurrencyToken);
        });
        Test("dataset validation and assignment expected artifacts have separate identities", () =>
        {
            using var db = Tasks();
            Unique(Model(db), typeof(SqlDatasetEngineValidation), "DatasetVersionId", "EngineProfileId");
            Unique(Model(db), typeof(SqlExpectedArtifact), "EngineTargetId");
            Unique(Model(db), typeof(SqlExpectedArtifact), "ArtifactKey");
        });
        Test("SQL private documents use dedicated jsonb columns", () =>
        {
            using var db = Tasks();
            foreach (var type in Model(db).GetEntityTypes().Where(x => x.ClrType.Namespace == typeof(SqlDataset).Namespace))
                foreach (var prop in type.GetProperties().Where(x => x.Name.EndsWith("Json", StringComparison.Ordinal)))
                    Assert(prop.GetColumnType() == "jsonb");
        });
        Test("private datasets require an owner at SaveChanges", () =>
        {
            using var db = Tasks();
            db.SqlDatasets.Add(new SqlDataset { Name = "private", OwnerUserId = null });
            Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        Test("private datasets require an owner at SaveChangesAsync", () =>
        {
            using var db = Tasks();
            db.SqlDatasets.Add(new SqlDataset { Name = "private", OwnerUserId = null });
            Throws<InvalidOperationException>(() => db.SaveChangesAsync().GetAwaiter().GetResult());
        });
        Test("private catalog denies another editor and every learner", () =>
        {
            var owner = Guid.NewGuid();
            var data = new SqlDataset { Name = "private", OwnerUserId = owner };
            Assert(SqlDatasetAccessPolicy.CanManage(data, owner, true, false));
            Assert(!SqlDatasetAccessPolicy.CanManage(data, Guid.NewGuid(), true, false));
            Assert(!SqlDatasetAccessPolicy.CanManage(data, owner, false, false));
            Assert(SqlDatasetAccessPolicy.CanManage(data, Guid.NewGuid(), false, true));
        });
        Test("shared editor library is not a public learner catalog", () =>
        {
            var data = new SqlDataset { AccessScope = SqlDatasetScopes.EditorLibrary };
            Assert(SqlDatasetAccessPolicy.CanReuse(data, Guid.NewGuid(), true, false));
            Assert(!SqlDatasetAccessPolicy.CanReuse(data, Guid.NewGuid(), false, false));
            Assert(!SqlDatasetAccessPolicy.CanReuse(data, null, true, true));
        });
        Test("only owner/admin can change sharing; archived datasets cannot be reused", () =>
        {
            var data = new SqlDataset { OwnerUserId = Guid.NewGuid(), AccessScope = SqlDatasetScopes.EditorLibrary };
            Assert(!SqlDatasetAccessPolicy.CanChangeSharing(data, Guid.NewGuid(), true, false));
            Assert(SqlDatasetAccessPolicy.CanChangeSharing(data, data.OwnerUserId, true, false));
            data.IsArchived = true;
            Assert(!SqlDatasetAccessPolicy.CanReuse(data, data.OwnerUserId, true, true));
        });
        Test("dataset hashes are calculated on insertion", () =>
        {
            using var db = Tasks();
            var version = Dataset();
            version.ContentHash = "untrusted input";
            db.SqlDatasetVersions.Add(version);
            db.SaveChanges();
            Assert(version.ContentHash == SqlContentKeys.Dataset(version));
        });
        Test("engine fingerprints are calculated on insertion", () =>
        {
            using var db = Tasks();
            var profile = Profile();
            profile.Fingerprint = "untrusted input";
            db.SqlEngineProfiles.Add(profile);
            db.SaveChanges();
            Assert(profile.Fingerprint == SqlContentKeys.Profile(profile));
        });
        Test("mutable updates rotate stamp and retain original stamp for concurrency", () =>
        {
            using var db = Tasks();
            var data = new SqlDataset { Name = "v1", OwnerUserId = Guid.NewGuid() };
            db.Attach(data);
            var previous = data.ConcurrencyStamp;
            data.Name = "v2";
            db.SaveChanges();
            Assert(data.ConcurrencyStamp != previous);
            Assert(db.Entry(data).Property(x => x.ConcurrencyStamp).OriginalValue == previous);
        });
        Test("dataset revision updates are rejected before persistence", () =>
        {
            using var db = Tasks();
            var version = Dataset();
            db.Attach(version);
            version.SeedJson = "{\"rows\":[1]}";
            Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        Test("spec revision deletion is rejected before persistence", () =>
        {
            using var db = Tasks();
            var spec = Spec(Dataset());
            db.Attach(spec);
            db.Remove(spec);
            Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        Test("runtime profile mutation is rejected", () =>
        {
            using var db = Tasks();
            var profile = Profile();
            db.Attach(profile);
            profile.EngineVersion = "changed";
            Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        Test("late engine-target insertion cannot mutate a stored spec's target set", () =>
        {
            using var db = Tasks();
            db.SqlAssignmentEngineTargets.Add(new SqlAssignmentEngineTarget { SpecVersionId = Guid.NewGuid(), EngineProfileId = Guid.NewGuid() });
            Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        Test("engine targets and revision are inserted and hashed together", () =>
        {
            using var db = Tasks();
            var spec = Spec(Dataset());
            var target = Target(spec, Profile());
            db.Add(spec);
            db.Add(target);
            db.SaveChanges();
            Assert(spec.SpecHash == SqlContentKeys.Spec(spec, new[] { target }));
        });
        Test("duplicate profiles within a revision are rejected", () =>
        {
            var spec = Spec(Dataset());
            var profile = Profile();
            Throws<ArgumentException>(() => SqlContentKeys.Spec(spec, new[] { Target(spec, profile), Target(spec, profile) }));
        });
        Test("canonical hashes ignore object property order and JSON whitespace", () =>
        {
            var a = Dataset();
            var b = Dataset();
            a.DefinitionJson = "{\"b\":2,\"a\":1}";
            b.DefinitionJson = "{ \"a\" : 1, \"b\" : 2 }";
            Assert(SqlContentKeys.Dataset(a) == SqlContentKeys.Dataset(b));
        });
        Test("canonical hashes normalize exact decimal numbers without float loss", () =>
        {
            var a = Dataset();
            var b = Dataset();
            a.SeedJson = "{\"x\":1.20,\"zero\":-0,\"big\":9007199254740993}";
            b.SeedJson = "{\"big\":9007199254740993.0,\"zero\":0,\"x\":12e-1}";
            Assert(SqlContentKeys.Dataset(a) == SqlContentKeys.Dataset(b));
            b.SeedJson = "{\"x\":1.2,\"zero\":0,\"big\":9007199254740992}";
            Assert(SqlContentKeys.Dataset(a) != SqlContentKeys.Dataset(b));
        });
        Test("canonical hashes preserve arrays, null semantics and strings", () =>
        {
            var a = Dataset();
            var b = Dataset();
            a.SeedJson = "{\"rows\":[1,null,\"1\"]}";
            b.SeedJson = "{\"rows\":[\"1\",null,1]}";
            Assert(SqlContentKeys.Dataset(a) != SqlContentKeys.Dataset(b));
        });
        Test("duplicate JSON keys fail instead of depending on parser last-write-wins", () =>
        {
            var data = Dataset();
            data.DefinitionJson = "{\"a\":1,\"a\":2}";
            Throws<ArgumentException>(() => SqlContentKeys.Dataset(data));
        });
        Test("out-of-bounds numeric exponents fail", () =>
        {
            var data = Dataset();
            data.SeedJson = "{\"a\":1e999999}";
            Throws<ArgumentException>(() => SqlContentKeys.Dataset(data));
        });
        Test("materialization deduplicates dataset content, not dataset/assignment ids", () =>
        {
            var a = Dataset();
            var b = Dataset();
            var profile = Profile();
            Assert(a.Id != b.Id && a.DatasetId != b.DatasetId);
            Assert(SqlContentKeys.Materialization(a, profile) == SqlContentKeys.Materialization(b, profile));
        });
        Test("engine and adapter changes invalidate materialization identity", () =>
        {
            var data = Dataset();
            var profile = Profile();
            var before = SqlContentKeys.Materialization(data, profile);
            profile.AdapterVersion = "2";
            profile.Fingerprint = SqlContentKeys.Profile(profile);
            Assert(before != SqlContentKeys.Materialization(data, profile));
            before = SqlContentKeys.Materialization(data, profile);
            profile.Engine = SqlEngineNames.MySql;
            profile.Fingerprint = SqlContentKeys.Profile(profile);
            Assert(before != SqlContentKeys.Materialization(data, profile));
        });
        Test("dataset-level engine overrides invalidate materialization", () =>
        {
            var data = Dataset();
            var profile = Profile();
            var before = SqlContentKeys.Materialization(data, profile);
            data.EngineOverridesJson = "{\"mysql\":{\"default\":\"changed\"}}";
            data.ContentHash = SqlContentKeys.Dataset(data);
            Assert(before != SqlContentKeys.Materialization(data, profile));
        });
        Test("two assignments sharing one dataset never share an expected artifact", () =>
        {
            var data = Dataset();
            var profile = Profile();
            var one = Spec(data);
            var two = Spec(data);
            var firstTarget = Target(one, profile);
            var secondTarget = Target(two, profile);
            one.SpecHash = SqlContentKeys.Spec(one, new[] { firstTarget });
            two.SpecHash = SqlContentKeys.Spec(two, new[] { secondTarget });
            Assert(SqlContentKeys.Expected(one, firstTarget, data, profile) != SqlContentKeys.Expected(two, secondTarget, data, profile));
        });
        Test("per-engine reference and comparison overrides enter the spec hash", () =>
        {
            var spec = Spec(Dataset());
            var target = Target(spec, Profile());
            var before = SqlContentKeys.Spec(spec, new[] { target });
            target.ReferenceSqlOverride = "SELECT 2";
            Assert(before != SqlContentKeys.Spec(spec, new[] { target }));
            before = SqlContentKeys.Spec(spec, new[] { target });
            target.ResultComparisonSettingsOverrideJson = "{\"orderMatters\":true}";
            Assert(before != SqlContentKeys.Spec(spec, new[] { target }));
        });
        Test("expected key rejects mismatched dataset and target bindings", () =>
        {
            var data = Dataset();
            var profile = Profile();
            var spec = Spec(data);
            var target = Target(spec, profile);
            spec.SpecHash = SqlContentKeys.Spec(spec, new[] { target });
            Throws<ArgumentException>(() => SqlContentKeys.Expected(spec, target, Dataset(), profile));
        });
        Test("private spec and expected payload fields cannot serialize accidentally", () =>
        {
            var spec = Spec(Dataset());
            spec.ReferenceSql = "private-reference";
            spec.StateCheckSettingsJson = "{\"secret\":true}";
            var json = JsonSerializer.Serialize(spec);
            Assert(!json.Contains("private-reference", StringComparison.Ordinal) && !json.Contains("secret", StringComparison.Ordinal));
            var expected = JsonSerializer.Serialize(new SqlExpectedArtifact { ExpectedJson = "{\"private-answer\":42}", ObjectKey = "private-key" });
            Assert(!expected.Contains("private-answer", StringComparison.Ordinal) && !expected.Contains("private-key", StringComparison.Ordinal));
        });
        Test("a valid expected receipt requires actual content metadata", () =>
        {
            using var db = Tasks();
            db.Add(new SqlExpectedArtifact
            {
                EngineTargetId = Guid.NewGuid(), ArtifactKey = new string('a', 64),
                Status = SqlValidationStatuses.Valid, ValidatedAt = DateTimeOffset.UtcNow
            });
            Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        Test("validated expected content cannot be overwritten in place", () =>
        {
            using var db = Tasks();
            var artifact = new SqlExpectedArtifact
            {
                EngineTargetId = Guid.NewGuid(), ArtifactKey = new string('a', 64),
                Status = SqlValidationStatuses.Valid, ValidatedAt = DateTimeOffset.UtcNow,
                ExpectedJson = "{\"rows\":[]}", ContentHash = new string('b', 64), ByteLength = 11
            };
            db.Attach(artifact);
            artifact.ExpectedJson = "{\"rows\":[1]}";
            Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        Test("artifact object references cannot be URLs or traversals", () =>
        {
            foreach (var key in new[] { "https://example.invalid/answer", "../answer", "/answer" })
            {
                using var db = Tasks();
                db.Add(new SqlExpectedArtifact { EngineTargetId = Guid.NewGuid(), ArtifactKey = new string('a', 64), ObjectKey = key });
                Throws<InvalidOperationException>(() => db.SaveChanges());
            }
        });
        Test("legacy execution rows default to legacy, never guessed as SQL", () =>
        {
            using var db = new ExecutionDbContextFactory().CreateDbContext(Array.Empty<string>());
            Assert(new ExecutionJob().Kind == ExecutionJobKinds.Legacy);
            var job = Model(db).FindEntityType(typeof(ExecutionJob))!;
            Assert((string)job.FindProperty("Kind")!.GetDefaultValue()! == ExecutionJobKinds.Legacy);
            Assert(job.FindProperty("PayloadJson")!.IsNullable && job.FindProperty("PayloadVersion")!.IsNullable);
        });
        Test("preview/materialization can exist without a graded submission", () =>
        {
            using var db = new ExecutionDbContextFactory().CreateDbContext(Array.Empty<string>());
            var job = Model(db).FindEntityType(typeof(ExecutionJob))!;
            Assert(job.FindProperty("SubmissionId")!.IsNullable);
            var checks = job.GetCheckConstraints().ToDictionary(x => x.Name ?? throw new InvalidOperationException("A SQL check constraint has no name."), x => x.Sql);
            Assert(checks["CK_ExecutionJobs_SqlSubmission"].Contains("'sql-preview', 'sql-materialize'", StringComparison.Ordinal));
            Assert(checks["CK_ExecutionJobs_SqlNoLegacyTests"].Contains("\"TestsJson\" IS NULL", StringComparison.Ordinal));
        });
        Test("execution routing, deduplication and lease columns are mapped", () =>
        {
            using var db = new ExecutionDbContextFactory().CreateDbContext(Array.Empty<string>());
            var type = Model(db).FindEntityType(typeof(ExecutionJob))!;
            foreach (var field in new[] { "Kind", "Target", "PayloadVersion", "PayloadJson", "DeduplicationKey", "ClaimedByWorkerId", "LeaseToken", "LeaseExpiresAt" })
                Assert(type.FindProperty(field) is not null);
            Unique(Model(db), typeof(ExecutionJob), "DeduplicationKey");
        });
        Test("submission engine/spec bindings are nullable for legacy and write-once", () =>
        {
            using var db = new SolutionsDbContextFactory().CreateDbContext(Array.Empty<string>());
            var type = Model(db).FindEntityType(typeof(SolutionSubmission))!;
            foreach (var field in new[] { "ExecutionTarget", "SqlSpecVersionId", "SqlEngineProfileId" })
            {
                Assert(type.FindProperty(field)!.IsNullable);
                Assert(type.FindProperty(field)!.GetAfterSaveBehavior() == PropertySaveBehavior.Throw);
            }
            Assert(type.GetForeignKeys().Count() == 0);
        });

        Console.WriteLine($"SQL domain checks: {passed} passed, {failed} failed. No migrations or database writes were performed.");
        return failed == 0 ? 0 : 1;
    }

    private static TasksDbContext Tasks() => new(new DbContextOptionsBuilder<TasksDbContext>()
        .UseNpgsql(OfflineConnection).AddInterceptors(new SuppressDatabaseSave()).Options);

    private static IModel Model(DbContext db) => db.GetService<IDesignTimeModel>().Model;

    private static SqlDatasetVersion Dataset()
    {
        var data = new SqlDatasetVersion { DatasetId = Guid.NewGuid(), Version = 1, DefinitionJson = "{\"tables\":[]}", SeedJson = "{\"tables\":{}}" };
        data.ContentHash = SqlContentKeys.Dataset(data);
        return data;
    }

    private static SqlEngineProfile Profile()
    {
        var profile = new SqlEngineProfile
        {
            Key = "postgresql-fixture", DisplayName = "Fixture only", Engine = SqlEngineNames.PostgreSql,
            EngineVersion = "fixture", RuntimeDigest = "sha256:" + new string('a', 64), AdapterVersion = "1"
        };
        profile.Fingerprint = SqlContentKeys.Profile(profile);
        return profile;
    }

    private static SqlAssignmentSpecVersion Spec(SqlDatasetVersion data) => new()
    {
        AssignmentId = Guid.NewGuid(), Version = 1, DatasetVersionId = data.Id, ReferenceSql = "SELECT 1"
    };

    private static SqlAssignmentEngineTarget Target(SqlAssignmentSpecVersion spec, SqlEngineProfile profile)
        => new() { SpecVersionId = spec.Id, EngineProfileId = profile.Id };

    private static void Unique(IModel model, Type entityType, params string[] properties)
        => Assert(model.FindEntityType(entityType)!.GetIndexes().Any(x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(properties)));

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
    }

    private sealed class SuppressDatabaseSave : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            => InterceptionResult<int>.SuppressWithResult(0);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}
