namespace taskforge.Helpers;

public static class Base64Helper
{
    public static byte[] DecodeDataUrlOrBase64(string base64OrDataUrl)
    {
        if (string.IsNullOrWhiteSpace(base64OrDataUrl))
            throw new ArgumentException("Empty base64", nameof(base64OrDataUrl));

        var s = base64OrDataUrl.Trim();

        // data:image/png;base64,AAAA...
        var comma = s.IndexOf(',');
        if (comma >= 0 && s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            s = s[(comma + 1)..];

        return Convert.FromBase64String(s);
    }
}
