using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>Versioned, length-unambiguous content identities. No runtime paths enter these keys.</summary>
public static class SqlContentKeys
{
    public const int MaxDocumentCharacters = 4 * 1024 * 1024;

    public static string Dataset(SqlDatasetVersion version) => Hash("dataset/1", new
    {
        version.DefinitionSchemaVersion,
        definition = ReadObject(version.DefinitionJson),
        seed = ReadObject(version.SeedJson),
        engineOverrides = ReadObject(version.EngineOverridesJson)
    });

    public static string Profile(SqlEngineProfile profile) => Hash("engine-profile/1", new
    {
        profile.Engine,
        profile.EngineVersion,
        profile.RuntimeDigest,
        profile.AdapterVersion,
        profile.SettingsSchemaVersion,
        settings = ReadObject(profile.SettingsJson)
    });

    public static string Spec(SqlAssignmentSpecVersion version, IEnumerable<SqlAssignmentEngineTarget> targets)
    {
        var ordered = targets.OrderBy(x => x.EngineProfileId).ToArray();
        if (ordered.Any(x => x.SpecVersionId != version.Id))
            throw new ArgumentException("An engine target belongs to another spec revision.", nameof(targets));
        if (ordered.Select(x => x.EngineProfileId).Distinct().Count() != ordered.Length)
            throw new ArgumentException("A spec revision cannot contain duplicate engine profiles.", nameof(targets));
        return Hash("assignment-spec/1", new
        {
            version.ContractVersion,
            version.DatasetVersionId,
            version.Mode,
            version.StarterSql,
            version.ReferenceSql,
            version.AllowMultipleStatements,
            result = ReadObject(version.ResultComparisonSettingsJson),
            state = ReadObject(version.StateCheckSettingsJson),
            schema = ReadObject(version.SchemaCheckSettingsJson),
            limits = ReadObject(version.LimitsJson),
            version.VerifierVersion,
            targets = ordered.Select(x => new
            {
                x.EngineProfileId,
                x.Enabled,
                x.Sort,
                x.StarterSqlOverride,
                x.ReferenceSqlOverride,
                result = ReadOptionalObject(x.ResultComparisonSettingsOverrideJson),
                state = ReadOptionalObject(x.StateCheckSettingsOverrideJson),
                schema = ReadOptionalObject(x.SchemaCheckSettingsOverrideJson)
            })
        });
    }

    public static string Materialization(SqlDatasetVersion dataset, SqlEngineProfile profile)
    {
        RequireHash(dataset.ContentHash, nameof(dataset.ContentHash));
        RequireHash(profile.Fingerprint, nameof(profile.Fingerprint));
        return Hash("materialization/1", new { dataset = dataset.ContentHash, profile = profile.Fingerprint });
    }

    public static string Expected(SqlAssignmentSpecVersion spec, SqlAssignmentEngineTarget target,
        SqlDatasetVersion dataset, SqlEngineProfile profile, int formatVersion = 1)
    {
        if (spec.Id != target.SpecVersionId || spec.DatasetVersionId != dataset.Id || target.EngineProfileId != profile.Id)
            throw new ArgumentException("Expected artifact bindings must refer to the same spec, dataset and engine profile.");
        if (formatVersion < 1) throw new ArgumentOutOfRangeException(nameof(formatVersion));
        RequireHash(spec.SpecHash, nameof(spec.SpecHash));
        return Hash("expected-artifact/1", new
        {
            specVersionId = spec.Id,
            spec = spec.SpecHash,
            engineTargetId = target.Id,
            materialization = Materialization(dataset, profile),
            spec.VerifierVersion,
            formatVersion
        });
    }

    public static string Hash(string purpose, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var envelope = JsonSerializer.SerializeToElement(new { format = "taskforge-sql-content-key/1", purpose, value });
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) WriteCanonical(writer, envelope);
        return Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant();
    }

    public static JsonElement ReadObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxDocumentCharacters)
            throw new ArgumentException("A SQL document must be a nonempty JSON object within the document size limit.");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A SQL document must have an object root.");
        return doc.RootElement.Clone();
    }

    public static JsonElement? ReadOptionalObject(string? json) => json is null ? null : ReadObject(json);

    public static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void RequireHash(string value, string field)
    {
        if (!IsHash(value)) throw new ArgumentException($"{field} must be a lowercase SHA-256 hex digest.", field);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                string? previous = null;
                foreach (var property in properties)
                {
                    if (previous == property.Name) throw new ArgumentException("Duplicate JSON property names are not allowed.");
                    previous = property.Name;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                // Exact base-ten normalization avoids float rounding and jsonb spelling drift.
                writer.WriteRawValue(NormalizeNumber(value.GetRawText()));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string NormalizeNumber(string token)
    {
        var negative = token[0] == '-';
        var unsigned = negative ? token[1..] : token;
        var exponentIndex = unsigned.IndexOfAny(new[] { 'e', 'E' });
        var mantissa = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var exponent = 0;
        if (exponentIndex >= 0 && (!int.TryParse(unsigned[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) || exponent is < -10000 or > 10000))
            throw new ArgumentException("SQL document numeric exponents must be between -10000 and 10000.");
        var dot = mantissa.IndexOf('.');
        var fractionalDigits = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        var significant = digits.TrimEnd('0');
        exponent = checked(exponent - fractionalDigits + digits.Length - significant.Length);
        return (negative ? "-" : string.Empty) + significant + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
