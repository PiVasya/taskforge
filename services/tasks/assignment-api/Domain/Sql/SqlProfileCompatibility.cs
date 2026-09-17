using System.Text.Json.Nodes;
using TaskForge.Sql;

namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>
/// Compares immutable SQL profiles by execution semantics rather than worker build identity.
/// The legacy bridge is intentionally narrow: it only recognizes the old go-native-v1
/// profiles whose executor fingerprint was embedded into SettingsJson (and, for SQLite,
/// RuntimeDigest). New profiles carry an explicit executionSemanticsVersion instead.
/// </summary>
public static class SqlProfileCompatibility
{
    public const string CurrentExecutionSemanticsVersion = "sql-runtime-v1";
    // Historical go-native-v1 profiles predate the explicit field, but they belong
    // permanently to v1. Do not point this at CurrentExecutionSemanticsVersion when
    // a future runtime bumps to v2.
    private const string LegacyGoExecutionSemanticsVersion = "sql-runtime-v1";
    private const string GoImplementation = "go-native-v1";

    // TaskForge has published immutable assignments against three audited MySQL
    // runtime identities during the 8.4 LTS rollout. The 8.4.11 OL9 image is the
    // historical production runtime used by existing assignments; r70 introduced
    // the 8.4.0 OL9/OL8 CPU pair. These exact identities share the same execution
    // semantics/client contract and are intentionally bridged so immutable published
    // profiles remain executable after failover or runtime-family upgrades. Unknown
    // 8.4.x versions/digests remain incompatible.
    private static readonly HashSet<string> CertifiedMySqlRuntimeIdentities = new(StringComparer.Ordinal)
    {
        "8.4.11|sha256:3466ba4a4828aa8d46fb7c3bc16b67b781c98413cf4ea0fac6feaa6e881faa26", // historical OL9
        "8.4.0|sha256:dab7049abafe3a0e12cbe5e49050cf149881c0cd9665c289e5808b9dad39c9e0", // r70 OL9
        "8.4.0|sha256:f7a8e140a7d6d1e6e0c99eeb0489c50a186ee4ac44ff55323a176529b9a43d33"  // r70 OL8 / cpuv1
    };

    public static bool IsCompatible(SqlEngineProfile current, SqlEngineProfile candidate)
    {
        if (!string.Equals(current.Key, candidate.Key, StringComparison.Ordinal)
            || !string.Equals(current.Engine, candidate.Engine, StringComparison.Ordinal)
            || !EngineVersionsCompatible(current, candidate)
            || !string.Equals(current.AdapterVersion, candidate.AdapterVersion, StringComparison.Ordinal)
            || current.SettingsSchemaVersion != candidate.SettingsSchemaVersion)
            return false;

        if (!TryNormalize(current, out var left) || !TryNormalize(candidate, out var right)
            || !string.Equals(left.SemanticsVersion, right.SemanticsVersion, StringComparison.Ordinal))
            return false;

        if (!RuntimeCompatible(current, left, candidate, right)) return false;

        // Historical profiles did not carry these stronger component identities. When one
        // side is legacy we compare only evidence that existed in that historical profile.
        // Once both sides are new profiles these fields participate normally.
        if (left.Legacy || right.Legacy)
        {
            left.Settings.Remove("clientRuntimeDigest");
            right.Settings.Remove("clientRuntimeDigest");
            left.Settings.Remove("unicodeVersion");
            right.Settings.Remove("unicodeVersion");
        }

        return string.Equals(
            SqlContentKeys.Hash("engine-compatibility-settings/1", left.Settings),
            SqlContentKeys.Hash("engine-compatibility-settings/1", right.Settings),
            StringComparison.Ordinal);
    }

    private static bool RuntimeCompatible(SqlEngineProfile leftProfile, Normalized left,
        SqlEngineProfile rightProfile, Normalized right)
    {
        if (!string.Equals(leftProfile.Engine, "sqlite", StringComparison.Ordinal))
        {
            if (string.Equals(leftProfile.RuntimeDigest, rightProfile.RuntimeDigest, StringComparison.Ordinal))
                return true;

            // Runtime identity remains immutable. The compatibility endpoint exposes
            // aliases only when both profiles belong to the audited TaskForge family;
            // all semantic settings are still compared below before compatibility is
            // accepted.
            if (CertifiedMySqlRuntime(leftProfile) && CertifiedMySqlRuntime(rightProfile))
                return true;

            return false;
        }

        // Old SQLite used sha256(executorFingerprint) as RuntimeDigest. That value described
        // the whole worker build, not SQLite itself. It can only be ignored when the profile
        // exactly matches that known legacy shape. New SQLite profiles use a component digest
        // and compare it strictly with other new profiles.
        if (!left.Legacy && !right.Legacy)
            return string.Equals(leftProfile.RuntimeDigest, rightProfile.RuntimeDigest, StringComparison.Ordinal);

        if (left.Legacy && !LegacySqliteDigestMatches(leftProfile, left.LegacyExecutorFingerprint)) return false;
        if (right.Legacy && !LegacySqliteDigestMatches(rightProfile, right.LegacyExecutorFingerprint)) return false;
        return true;
    }


    private static bool EngineVersionsCompatible(SqlEngineProfile left, SqlEngineProfile right)
    {
        if (string.Equals(left.EngineVersion, right.EngineVersion, StringComparison.Ordinal))
            return true;
        return CertifiedMySqlRuntime(left) && CertifiedMySqlRuntime(right);
    }

    private static bool CertifiedMySqlRuntime(SqlEngineProfile profile)
        => string.Equals(profile.Engine, "mysql", StringComparison.Ordinal)
            && CertifiedMySqlRuntimeIdentities.Contains(profile.EngineVersion + "|" + profile.RuntimeDigest);

    private static bool LegacySqliteDigestMatches(SqlEngineProfile profile, string? executorFingerprint)
        => SqlWire.IsHash(executorFingerprint)
            && string.Equals(profile.RuntimeDigest, "sha256:" + executorFingerprint, StringComparison.Ordinal);

    private static bool TryNormalize(SqlEngineProfile profile, out Normalized normalized)
    {
        normalized = null!;
        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(profile.SettingsJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return false;
        }

        var implementation = StringValue(settings, "implementation");
        var executor = StringValue(settings, "executorFingerprint");
        var semantics = StringValue(settings, "executionSemanticsVersion");
        var legacy = string.IsNullOrWhiteSpace(semantics)
            && string.Equals(implementation, GoImplementation, StringComparison.Ordinal)
            && SqlWire.IsHash(executor);

        if (legacy)
        {
            semantics = LegacyGoExecutionSemanticsVersion;
            settings["executionSemanticsVersion"] = semantics;
        }
        else if (string.IsNullOrWhiteSpace(semantics))
        {
            return false;
        }

        settings.Remove("executorFingerprint");

        if (settings["clientLibraries"] is JsonObject libraries)
        {
            if (settings.ContainsKey("clientLibraryVersion")) return false;
            var key = profile.Engine switch
            {
                "postgresql" => "libpq",
                "mysql" => "mariadbConnectorC",
                "sqlite" => "sqlite",
                _ => string.Empty
            };
            var version = StringValue(libraries, key);
            if (string.IsNullOrWhiteSpace(version)) return false;
            settings.Remove("clientLibraries");
            settings["clientLibraryVersion"] = version;
        }

        if (settings["compileOptions"] is JsonArray options)
        {
            var sorted = new List<string>(options.Count);
            foreach (var item in options)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var text)) return false;
                sorted.Add(text);
            }
            sorted.Sort(StringComparer.Ordinal);
            var replacement = new JsonArray();
            foreach (var option in sorted) replacement.Add(option);
            settings["compileOptions"] = replacement;
        }

        normalized = new Normalized(settings, legacy, executor, semantics!);
        return true;
    }

    private static string? StringValue(JsonObject source, string key)
        => source[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed record Normalized(JsonObject Settings, bool Legacy, string? LegacyExecutorFingerprint,
        string SemanticsVersion);
}
