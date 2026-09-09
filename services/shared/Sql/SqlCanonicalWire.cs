using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskForge.Sql;

// An explicitly specified cross-language checksum representation for expected content.
// ASCII JSON escapes (lowercase hex), UTF-16 ordinal key order, exact base-ten numbers.
// Independent of JSONB property order/whitespace and of serializer HTML-escape settings.
public static class SqlCanonicalWire
{
    public static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Encode(value)))).ToLowerInvariant();
    public static string Encode(JsonElement value)
    {
        var output = new StringBuilder();
        Write(output, value);
        return output.ToString();
    }
    private static void Text(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 32 || c >= 127) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
    private static void Write(StringBuilder output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var first = true;
                string? previous = null;
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (property.Name == previous) throw new ArgumentException("Duplicate JSON property.");
                    previous = property.Name;
                    if (!first) output.Append(',');
                    first = false;
                    Text(output, property.Name); output.Append(':'); Write(output, property.Value);
                }
                output.Append('}'); break;
            case JsonValueKind.Array:
                output.Append('['); var firstItem = true;
                foreach (var item in value.EnumerateArray())
                { if (!firstItem) output.Append(','); firstItem = false; Write(output, item); }
                output.Append(']'); break;
            case JsonValueKind.String: Text(output, value.GetString()!); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            case JsonValueKind.Number:
                var token = value.GetRawText();
                var negative = token[0] == '-';
                var unsigned = negative ? token[1..] : token;
                var at = unsigned.IndexOfAny(new[] { 'e', 'E' });
                var mantissa = at < 0 ? unsigned : unsigned[..at];
                var exponent = at < 0 ? 0 : int.Parse(unsigned[(at + 1)..], CultureInfo.InvariantCulture);
                if (exponent is < -10000 or > 10000) throw new ArgumentException("Numeric exponent exceeds the wire contract.");
                var dot = mantissa.IndexOf('.');
                var fraction = dot < 0 ? 0 : mantissa.Length - dot - 1;
                var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
                if (digits.Length == 0) { output.Append('0'); break; }
                var significant = digits.TrimEnd('0');
                if (negative) output.Append('-');
                output.Append(significant).Append('e').Append((exponent - fraction + digits.Length - significant.Length).ToString(CultureInfo.InvariantCulture));
                break;
            default: throw new ArgumentException("Unsupported JSON value.");
        }
    }
}
