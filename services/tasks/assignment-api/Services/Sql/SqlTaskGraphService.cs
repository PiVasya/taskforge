using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TaskForge.Sql;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Domain.Sql;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;

namespace TaskForge.Tasks.Api.Services.Sql;

internal sealed record SqlGraphDataset(string Key, string Name, string? Description, SqlDefinition Definition,
    Dictionary<string, List<Dictionary<string, JsonElement>>> Seed, Dictionary<string, SqlEngineMapping>? EngineOverrides = null);
internal sealed record SqlGraphTarget(SqlProfileRegistration Profile, bool Enabled = true, int Sort = 0,
    string? StarterSqlOverride = null, string? ReferenceSqlOverride = null, SqlComparison? ComparisonOverride = null,
    SqlStateCheck? StateCheckOverride = null, SqlSchemaCheck? SchemaCheckOverride = null);
internal sealed record SqlGraphSpec(string Dataset, string Mode, bool AllowMultipleStatements, SqlGraphTarget[] Targets,
    string? StarterSql = null, SqlLimits? Limits = null, string? ReferenceSql = null, SqlComparison? Comparison = null,
    SqlStateCheck? StateCheck = null, SqlSchemaCheck? SchemaCheck = null);
internal sealed record SqlGraphExport(IReadOnlyDictionary<Guid, JsonObject> Specs, JsonArray Datasets);

internal static class SqlTaskGraphService
{
    private static readonly string[] PrivateSpecFields = ["referenceSql", "comparison", "stateCheck", "schemaCheck"];
    private static readonly string[] PrivateTargetFields = ["referenceSqlOverride", "comparisonOverride", "stateCheckOverride", "schemaCheckOverride"];

    internal static List<ValidationIssue> Validate(JsonElement root, IReadOnlySet<string> scopes, int schemaVersion)
    {
        var issues = new List<ValidationIssue>();
        var hasDatasets = root.TryGetProperty("datasets", out var datasets);
        JsonElement[] tasks = root.TryGetProperty("tasks", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
        var sqlTasks = tasks.Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("sql", out var s) && s.ValueKind != JsonValueKind.Null).ToArray();
        for (var index = 0; index < tasks.Length; index++)
        {
            var task = tasks[index];
            if (task.ValueKind == JsonValueKind.Object && task.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "sql-test" && (scopes.Contains("content") || scopes.Contains("checks"))
                && (!task.TryGetProperty("sql", out var spec) || spec.ValueKind != JsonValueKind.Object))
                issues.Add(new($"$.tasks[{index}].sql", "SQL content/check imports require a dedicated SQL spec and dataset reference."));
        }
        if (schemaVersion < 5)
        {
            if (hasDatasets || sqlTasks.Length > 0) issues.Add(new("$.datasets", "SQL resources require schemaVersion 5."));
            return issues;
        }
        if (!hasDatasets || datasets.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("$.datasets", "schemaVersion 5 requires a datasets array (empty is allowed)."));
            return issues;
        }
        if (datasets.GetArrayLength() > 256 || datasets.GetRawText().Length > 16_000_000)
        {
            issues.Add(new("$.datasets", "SQL resources exceed the graph import budget (256 resources / 16 MB)."));
            return issues;
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var node in datasets.EnumerateArray())
        {
            try
            {
                var data = SqlWire.Read<SqlGraphDataset>(node);
                if (data.Key is null || !Regex.IsMatch(data.Key, "^[a-zA-Z0-9_.-]{1,80}$") || !keys.Add(data.Key)) throw new ArgumentException("Dataset keys must be unique, nonempty references.");
                if (string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 200 || (data.Description?.Length ?? 0) > 8000) throw new ArgumentException("Invalid dataset name or description.");
                SqlPortableValidator.Definition(new(data.Definition, data.Seed, data.EngineOverrides));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException) { issues.Add(new($"$.datasets[{i}]", ex.Message)); }
            i++;
        }
        for (i = 0; i < tasks.Length; i++)
        {
            var node = tasks[i];
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("sql", out var raw) || raw.ValueKind == JsonValueKind.Null) continue;
            try
            {
                if (node.TryGetProperty("type", out var type) && type.GetString() != "sql-test") throw new ArgumentException("Only sql-test can contain a SQL specification.");
                var spec = SqlWire.Read<SqlGraphSpec>(raw);
                if (spec.Dataset is null || !keys.Contains(spec.Dataset)) throw new ArgumentException("SQL dataset reference is not declared in datasets[].");
                if (spec.Mode is not ("result" or "state" or "schema") || spec.Targets is null || spec.Targets.Length is < 1 or > 12 || !spec.Targets.Any(x => x is not null && x.Enabled)) throw new ArgumentException("Invalid SQL mode or engine target list.");
                (spec.Limits ?? new()).Validate(); (spec.Comparison ?? new()).Validate();
                foreach (var target in spec.Targets)
                {
                    if (target is null || target.Profile is null) throw new ArgumentException("Engine targets must not be null.");
                    ValidateProfile(target.Profile);
                    target.ComparisonOverride?.Validate();
                    if ((target.ReferenceSqlOverride?.Length ?? 0) > SqlWire.MaxSourceLength || (target.StarterSqlOverride?.Length ?? 0) > SqlWire.MaxSourceLength) throw new ArgumentException("SQL source is too long.");
                }
                if ((spec.ReferenceSql?.Length ?? 0) > SqlWire.MaxSourceLength || (spec.StarterSql?.Length ?? 0) > SqlWire.MaxSourceLength) throw new ArgumentException("SQL source is too long.");
                if (!scopes.Contains("checks"))
                {
                    if (PrivateSpecFields.Any(p => raw.TryGetProperty(p, out _))) throw new ArgumentException("Private SQL checks require the checks scope.");
                    if (raw.TryGetProperty("targets", out var ts) && ts.EnumerateArray().Any(t => PrivateTargetFields.Any(p => t.TryGetProperty(p, out _)))) throw new ArgumentException("Private engine overrides require the checks scope.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException) { issues.Add(new($"$.tasks[{i}].sql", ex.Message)); }
        }
        return issues;
    }

    private static void ValidateProfile(SqlProfileRegistration profile)
    {
        if (profile.Engine is not ("postgresql" or "mysql" or "sqlite") || profile.AdapterVersion != SqlWire.AdapterVersion
            || string.IsNullOrWhiteSpace(profile.EngineVersion) || profile.EngineVersion.Length > 80
            || profile.RuntimeDigest is null || !profile.RuntimeDigest.StartsWith("sha256:") || !SqlWire.IsHash(profile.RuntimeDigest[7..])
            || profile.Settings.ValueKind != JsonValueKind.Object || profile.Settings.GetRawText().Length > 100_000)
            throw new ArgumentException("Unsupported exact SQL engine profile. Rebind it explicitly in the SQL editor.");
    }

    internal static async Task<SqlGraphExport> Export(TasksDbContext db, IReadOnlyList<Assignment> assignments, GraphExportOptions options, CancellationToken ct)
    {
        var specs = new Dictionary<Guid, JsonObject>();
        var datasets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!options.IncludeContent && !options.IncludeChecks) return new(specs, new());
        var ids = assignments.Where(x => x.Type == SqlTaskTypes.SqlTest).Select(x => x.Id).ToArray();
        var roots = await db.SqlAssignmentSpecs.AsNoTracking().Where(x => ids.Contains(x.AssignmentId)).ToListAsync(ct);
        foreach (var root in roots)
        {
            var versionId = root.DraftVersionId ?? root.PublishedVersionId;
            if (versionId is null) continue;
            var spec = await db.SqlAssignmentSpecVersions.AsNoTracking().SingleAsync(x => x.Id == versionId, ct);
            var data = await db.SqlDatasetVersions.AsNoTracking().SingleAsync(x => x.Id == spec.DatasetVersionId, ct);
            var owner = await db.SqlDatasets.AsNoTracking().SingleAsync(x => x.Id == data.DatasetId, ct);
            var key = "dataset-" + data.ContentHash; // Shared by content, never embedded into every task.
            datasets.TryAdd(key, new JsonObject { ["key"] = key, ["name"] = owner.Name, ["description"] = owner.Description,
                ["definition"] = JsonNode.Parse(data.DefinitionJson), ["seed"] = JsonNode.Parse(data.SeedJson), ["engineOverrides"] = JsonNode.Parse(data.EngineOverridesJson) });
            var targets = await db.SqlAssignmentEngineTargets.AsNoTracking().Where(x => x.SpecVersionId == spec.Id).OrderBy(x => x.Sort).ToListAsync(ct);
            var targetJson = new JsonArray();
            foreach (var target in targets)
            {
                var p = await db.SqlEngineProfiles.AsNoTracking().SingleAsync(x => x.Id == target.EngineProfileId, ct);
                var item = new JsonObject { ["profile"] = JsonSerializer.SerializeToNode(new SqlProfileRegistration(p.Engine, p.EngineVersion, p.RuntimeDigest, p.AdapterVersion,
                        JsonDocument.Parse(p.SettingsJson).RootElement.Clone()), SqlWire.Json), ["enabled"] = target.Enabled, ["sort"] = target.Sort };
                if (options.IncludeContent) item["starterSqlOverride"] = target.StarterSqlOverride;
                if (options.IncludeChecks)
                {
                    item["referenceSqlOverride"] = target.ReferenceSqlOverride;
                    item["comparisonOverride"] = target.ResultComparisonSettingsOverrideJson is null ? null : JsonNode.Parse(target.ResultComparisonSettingsOverrideJson);
                    item["stateCheckOverride"] = target.StateCheckSettingsOverrideJson is null ? null : JsonNode.Parse(target.StateCheckSettingsOverrideJson);
                    item["schemaCheckOverride"] = target.SchemaCheckSettingsOverrideJson is null ? null : JsonNode.Parse(target.SchemaCheckSettingsOverrideJson);
                }
                targetJson.Add(item);
            }
            var node = new JsonObject { ["dataset"] = key, ["mode"] = spec.Mode, ["allowMultipleStatements"] = spec.AllowMultipleStatements,
                ["targets"] = targetJson, ["limits"] = JsonNode.Parse(spec.LimitsJson) };
            if (options.IncludeContent) node["starterSql"] = spec.StarterSql;
            if (options.IncludeChecks)
            {
                node["referenceSql"] = spec.ReferenceSql;
                node["comparison"] = JsonNode.Parse(spec.ResultComparisonSettingsJson);
                node["stateCheck"] = JsonNode.Parse(spec.StateCheckSettingsJson);
                node["schemaCheck"] = JsonNode.Parse(spec.SchemaCheckSettingsJson);
            }
            specs[root.AssignmentId] = node;
        }
        var array = new JsonArray(datasets.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode)x.Value).ToArray());
        if (array.ToJsonString().Length > 16_000_000 || array.Count > 256) throw new ArgumentException("SQL export exceeds 256 datasets / 16 MB; export a smaller subtree.");
        return new(specs, array);
    }

    internal static async Task Import(TasksDbContext db, JsonElement graph, IReadOnlyList<(string? Key, string CourseRef, Assignment Assignment, string Action)> processed,
        GraphImportOptions options, Guid user, bool admin, CancellationToken ct)
    {
        if ((!options.UpdateContent && !options.UpdateChecks) || !graph.TryGetProperty("datasets", out var dataArray)) return;
        var tasks = graph.GetProperty("tasks").EnumerateArray().Where(x => x.TryGetProperty("sql", out var s) && s.ValueKind == JsonValueKind.Object)
            .ToDictionary(x => x.GetProperty("key").GetString()!, x => SqlWire.Read<SqlGraphSpec>(x.GetProperty("sql")), StringComparer.Ordinal);
        if (tasks.Count == 0) return;
        var referenced = tasks.Values.Select(x => x.Dataset).ToHashSet(StringComparer.Ordinal);
        var versionByKey = new Dictionary<string, SqlDatasetVersion>(StringComparer.Ordinal);
        foreach (var node in dataArray.EnumerateArray())
        {
            var input = SqlWire.Read<SqlGraphDataset>(node);
            if (!referenced.Contains(input.Key)) continue;
            var version = new SqlDatasetVersion { DefinitionJson = SqlWire.Serialize(input.Definition), SeedJson = SqlWire.Serialize(input.Seed),
                EngineOverridesJson = SqlWire.Serialize(input.EngineOverrides ?? new()) };
            var hash = SqlContentKeys.Dataset(version);
            var existing = await (from v in db.SqlDatasetVersions
                join d in db.SqlDatasets on v.DatasetId equals d.Id
                where v.ContentHash == hash && !d.IsArchived && (d.OwnerUserId == user || d.AccessScope == "editor-library" || admin)
                orderby v.CreatedAt select v).FirstOrDefaultAsync(ct);
            if (existing is not null) version = existing;
            else
            {
                var dataset = new SqlDataset { Name = input.Name, Description = input.Description, OwnerUserId = user, AccessScope = "private" };
                version.DatasetId = dataset.Id;
                db.SqlDatasets.Add(dataset); db.SqlDatasetVersions.Add(version);
                await db.SaveChangesAsync(ct);
            }
            versionByKey[input.Key] = version;
        }
        foreach (var item in processed)
        {
            if (item.Key is null || !tasks.TryGetValue(item.Key, out var source)) continue;
            if (item.Assignment.Type != SqlTaskTypes.SqlTest) throw new ArgumentException("SQL specifications cannot be imported into another assignment type.");
            var root = await db.SqlAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == item.Assignment.Id, ct);
            var old = root?.DraftVersionId is null ? null : await db.SqlAssignmentSpecVersions.AsNoTracking().SingleAsync(x => x.Id == root.DraftVersionId, ct);
            List<SqlAssignmentEngineTarget> oldTargets = old is null ? [] : await db.SqlAssignmentEngineTargets.AsNoTracking().Where(x => x.SpecVersionId == old.Id).ToListAsync(ct);
            var targets = new List<SqlTargetInput>();
            foreach (var input in source.Targets)
            {
                var profile = await ResolveProfile(db, input.Profile, ct);
                var previous = oldTargets.FirstOrDefault(x => x.EngineProfileId == profile.Id);
                targets.Add(new SqlTargetInput(profile.Id, input.Enabled, input.Sort,
                    options.UpdateContent ? input.StarterSqlOverride : previous?.StarterSqlOverride,
                    options.UpdateChecks ? input.ReferenceSqlOverride : previous?.ReferenceSqlOverride,
                    options.UpdateChecks ? input.ComparisonOverride : Read<SqlComparison>(previous?.ResultComparisonSettingsOverrideJson),
                    options.UpdateChecks ? input.StateCheckOverride : Read<SqlStateCheck>(previous?.StateCheckSettingsOverrideJson),
                    options.UpdateChecks ? input.SchemaCheckOverride : Read<SqlSchemaCheck>(previous?.SchemaCheckSettingsOverrideJson)));
            }
            if (!options.UpdateContent && old is not null)
            {
                var oldData = await db.SqlDatasetVersions.AsNoTracking().SingleAsync(x => x.Id == old.DatasetVersionId, ct);
                if (oldData.ContentHash != versionByKey[source.Dataset].ContentHash || old.Mode != source.Mode
                    || !oldTargets.Select(x => x.EngineProfileId).ToHashSet().SetEquals(targets.Select(x => x.EngineProfileId)))
                    throw new ArgumentException("SQL checks-only import requires the same dataset, mode and exact engine profiles. Include content to change that context.");
                targets = targets.Select(x => x with { Enabled = oldTargets.Single(t => t.EngineProfileId == x.EngineProfileId).Enabled,
                    Sort = oldTargets.Single(t => t.EngineProfileId == x.EngineProfileId).Sort }).ToList();
            }
            var request = new SqlSpecInput(options.UpdateContent || old is null ? versionByKey[source.Dataset].Id : old.DatasetVersionId,
                options.UpdateContent || old is null ? source.Mode : old.Mode,
                options.UpdateContent ? source.StarterSql : old?.StarterSql,
                options.UpdateChecks ? source.ReferenceSql : old?.ReferenceSql,
                options.UpdateContent || old is null ? source.AllowMultipleStatements : old.AllowMultipleStatements,
                options.UpdateChecks ? source.Comparison ?? new() : Read<SqlComparison>(old?.ResultComparisonSettingsJson) ?? new(),
                options.UpdateChecks ? source.StateCheck ?? new() : Read<SqlStateCheck>(old?.StateCheckSettingsJson) ?? new(),
                options.UpdateChecks ? source.SchemaCheck ?? new() : Read<SqlSchemaCheck>(old?.SchemaCheckSettingsJson) ?? new(),
                options.UpdateContent ? source.Limits ?? new() : Read<SqlLimits>(old?.LimitsJson) ?? new(), targets.ToArray(), root?.ConcurrencyStamp);
            await SqlTaskService.SaveSpec(db, item.Assignment.Id, request, user, admin, ct);
            // Imports create a draft and revalidate. Neither imported flags nor stale expected
            // artifacts can publish it; an already published revision remains authoritative.
            if (root?.PublishedVersionId is null) item.Assignment.IsVisible = false;
        }
        await db.SaveChangesAsync(ct);
    }

    private static T? Read<T>(string? json) where T : class => json is null ? null : JsonSerializer.Deserialize<T>(json, SqlWire.Json);

    private static async Task<SqlEngineProfile> ResolveProfile(TasksDbContext db, SqlProfileRegistration input, CancellationToken ct)
    {
        ValidateProfile(input);
        var candidate = new SqlEngineProfile { Key = input.Engine, Engine = input.Engine, EngineVersion = input.EngineVersion,
            RuntimeDigest = input.RuntimeDigest, AdapterVersion = input.AdapterVersion, SettingsJson = input.Settings.GetRawText(),
            DisplayName = $"{input.Engine} {input.EngineVersion}" };
        var hash = SqlContentKeys.Profile(candidate);
        var existing = await db.SqlEngineProfiles.FirstOrDefaultAsync(x => x.Fingerprint == hash, ct);
        if (existing is not null) return existing;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(704638913)", ct);
        existing = await db.SqlEngineProfiles.FirstOrDefaultAsync(x => x.Fingerprint == hash, ct);
        if (existing is not null) return existing;
        candidate.Revision = (await db.SqlEngineProfiles.Where(x => x.Key == input.Engine).MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1;
        db.SqlEngineProfiles.Add(candidate); await db.SaveChangesAsync(ct);
        return candidate;
    }
}
