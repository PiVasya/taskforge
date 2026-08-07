using System.Buffers.Binary;

namespace TaskForge.Browser.Api.Infrastructure;

public sealed record CachedBrowserArtifact(
    byte[] Bytes,
    int Width,
    int Height,
    bool FullPage,
    bool FullPageTruncated,
    bool Annotated);

public static class BrowserArtifactCacheCodec
{
    private static readonly byte[] Magic = "TFART001"u8.ToArray();
    private const int HeaderLength = 8 + 4 + 4 + 1;

    public static byte[] Encode(CachedBrowserArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var payload = artifact.Bytes ?? [];
        var result = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), artifact.Width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12, 4), artifact.Height);

        byte flags = 0;
        if (artifact.FullPage) flags |= 1;
        if (artifact.FullPageTruncated) flags |= 2;
        if (artifact.Annotated) flags |= 4;
        result[16] = flags;
        payload.CopyTo(result, HeaderLength);
        return result;
    }

    public static bool TryDecode(byte[]? value, out CachedBrowserArtifact artifact)
    {
        artifact = new CachedBrowserArtifact([], 0, 0, false, false, false);
        if (value is null || value.Length < HeaderLength || !value.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(8, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(12, 4));
        if (width <= 0 || height <= 0) return false;

        var flags = value[16];
        artifact = new CachedBrowserArtifact(
            value.AsSpan(HeaderLength).ToArray(),
            width,
            height,
            (flags & 1) != 0,
            (flags & 2) != 0,
            (flags & 4) != 0);
        return true;
    }
}
