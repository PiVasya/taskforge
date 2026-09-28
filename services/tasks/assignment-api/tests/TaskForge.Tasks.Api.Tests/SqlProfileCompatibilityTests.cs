using TaskForge.Tasks.Api.Domain.Sql;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class SqlProfileCompatibilityTests
{
    private const string HistoricalMySql8411 = "sha256:3466ba4a4828aa8d46fb7c3bc16b67b781c98413cf4ea0fac6feaa6e881faa26";
    private const string CpuV1MySql840 = "sha256:f7a8e140a7d6d1e6e0c99eeb0489c50a186ee4ac44ff55323a176529b9a43d33";

    [Fact]
    public void CpuV1Runtime_AdvertisesCertifiedHistoricalMySql8411Profile()
    {
        var current = MySql("8.4.0", CpuV1MySql840);
        var published = MySql("8.4.11", HistoricalMySql8411);
        var unknown = MySql("8.4.12", "sha256:" + new string('1', 64));
        var foreign = PostgreSql();

        var candidates = SqlProfileCompatibility.CandidateQuery(
            new[] { current, published, unknown, foreign }.AsQueryable(), current).ToArray();

        Assert.Contains(published, candidates);
        Assert.DoesNotContain(foreign, candidates);

        var compatible = candidates.Where(x => SqlProfileCompatibility.IsCompatible(current, x)).ToArray();
        Assert.Contains(published, compatible);
        Assert.DoesNotContain(unknown, compatible);
    }

    private static SqlEngineProfile MySql(string version, string digest) => new()
    {
        Key = "mysql",
        Engine = "mysql",
        EngineVersion = version,
        RuntimeDigest = digest,
        AdapterVersion = "1.0.0",
        SettingsSchemaVersion = 1,
        SettingsJson = """
            {
              "sqlMode":"STRICT_ALL_TABLES,ONLY_FULL_GROUP_BY,ERROR_FOR_DIVISION_BY_ZERO,NO_ZERO_DATE,NO_ZERO_IN_DATE,NO_ENGINE_SUBSTITUTION,NO_BACKSLASH_ESCAPES",
              "encoding":"utf8mb4",
              "timezone":"+00:00",
              "collation":"utf8mb4_0900_as_cs",
              "caseFolding":"unicode-default-v1",
              "serverBuild":"MySQL Community Server - GPL",
              "implementation":"go-native-v1",
              "unicodeVersion":"15.0.0",
              "storageStrategy":"logical-script-v1",
              "transactionMode":"autocommit",
              "identifierPolicy":"portable-lower-v1",
              "clientRuntimeDigest":"sha256:8ff2e2234d00e2b4c6dd10af81f123a64d5647300f966032cd9b59abfef40615",
              "lowerCaseTableNames":0,
              "clientLibraryVersion":"3.3.19",
              "executionSemanticsVersion":"sql-runtime-v2"
            }
            """
    };

    private static SqlEngineProfile PostgreSql() => new()
    {
        Key = "postgresql",
        Engine = "postgresql",
        EngineVersion = "18.6",
        RuntimeDigest = "sha256:" + new string('2', 64),
        AdapterVersion = "1.0.0",
        SettingsSchemaVersion = 1,
        SettingsJson = """{"implementation":"go-native-v1","executionSemanticsVersion":"sql-runtime-v2"}"""
    };
}
