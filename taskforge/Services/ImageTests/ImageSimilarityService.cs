using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace taskforge.Services.ImageTests;

/// <summary>
/// Pure-managed image similarity implementation.
///
/// Why:
/// - OpenCvSharp requires native dependencies (ffmpeg, OpenCV runtime) which frequently break inside
///   slim containers.
/// - For typical "did the student render the expected picture" checks, perceptual hashing is
///   sufficient and is dramatically easier to ship.
///
/// Algorithm:
/// - Decode images with ImageSharp (managed).
/// - Compute 64-bit difference hash (dHash) on a 9x8 grayscale thumbnail.
/// - Similarity% = 100 * (1 - HammingDistance(hashA, hashB)/64).
///
/// Notes:
/// - dHash is robust to small resizes / color shifts, but not to big rotations/crops.
/// - Keep your task's threshold realistic (e.g., 85-95 depending on how strict you want it).
/// </summary>
public sealed class ImageSimilarityService : IImageSimilarityService
{
    private readonly ILogger<ImageSimilarityService> _logger;
    private readonly IImageAnalyzerClient _analyzer;
    private readonly ImageAnalyzerOptions _opt;

    public ImageSimilarityService(
        ILogger<ImageSimilarityService> logger,
        IImageAnalyzerClient analyzer,
        IOptions<ImageAnalyzerOptions> opt)
    {
        _logger = logger;
        _analyzer = analyzer;
        _opt = opt.Value;
    }

    public async Task<double> GetSimilarityPercentAsync(
        Stream expected,
        Stream actual,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Read bytes once to support both: external analyzer and local fallback.
            var expBytes = await ReadAllBytesAsync(expected, ct);
            var actBytes = await ReadAllBytesAsync(actual, ct);

            // Prefer external analyzer (OpenCLIP) if configured.
            // IMPORTANT: when analyzer is enabled, we do NOT fall back to dHash (it's too weak for real tasks).
            // If analyzer is down, we throw a special exception so the API can return 503 and the front can show a notification.
            if (_opt.Enabled && !string.IsNullOrWhiteSpace(_opt.Url))
            {
                try
                {
                    var r = await _analyzer.CompareAsync(expBytes, actBytes, ct);
                    if (r is null)
                        throw new ImageAnalyzerUnavailableException("Analyzer returned empty result");

                    // Analyzer returns 0..1. Convert to 0..100.
                    var simPct = r.CombinedSimilarity * 100.0;
                    if (simPct < 0) simPct = 0;
                    if (simPct > 100) simPct = 100;
                    return simPct;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ImageAnalyzerUnavailableException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ImageSimilarity] External analyzer failed.");
                    throw new ImageAnalyzerUnavailableException("External analyzer failed", ex);
                }
            }

            // Local fallback: decode both images.
            using var expImg = await LoadAsRgbaAsync(new MemoryStream(expBytes), ct);
            using var actImg = await LoadAsRgbaAsync(new MemoryStream(actBytes), ct);

            var h1 = ComputeDHash64(expImg);
            var h2 = ComputeDHash64(actImg);

            var dist = HammingDistance(h1, h2);
            var similarity = (1.0 - (dist / 64.0)) * 100.0;

            // Clamp to [0..100]
            if (similarity < 0) similarity = 0;
            if (similarity > 100) similarity = 100;
            return similarity;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImageAnalyzerUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ImageSimilarity] Failed to compare images. Returning 0% similarity.");
            return 0;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream s, CancellationToken ct)
    {
        if (s is MemoryStream ms)
        {
            // Ensure position doesn't matter.
            return ms.ToArray();
        }

        if (s.CanSeek)
        {
            s.Position = 0;
        }

        using var buf = new MemoryStream();
        await s.CopyToAsync(buf, ct);
        return buf.ToArray();
    }

    private static async Task<Image<Rgba32>> LoadAsRgbaAsync(Stream s, CancellationToken ct)
    {
        // ImageSharp requires seekable stream for some formats; ensure we have one.
        if (s.CanSeek)
        {
            s.Position = 0;
            return await Image.LoadAsync<Rgba32>(s, ct);
        }

        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        ms.Position = 0;
        return await Image.LoadAsync<Rgba32>(ms, ct);
    }

    private static ulong ComputeDHash64(Image<Rgba32> src)
    {
        // dHash uses a 9x8 image so we can compare adjacent pixels horizontally
        // and produce 8*8 = 64 bits.
        using var img = src.Clone(ctx => ctx
            .Resize(new ResizeOptions
            {
                Size = new Size(9, 8),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Bicubic
            })
            .Grayscale());

        // Access pixels row by row.
        ulong hash = 0;
        var bitIndex = 0;

        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < 8; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < 8; x++)
                {
                    // After Grayscale(), R=G=B, so compare R.
                    var left = row[x].R;
                    var right = row[x + 1].R;
                    if (left < right)
                    {
                        hash |= (1UL << bitIndex);
                    }
                    bitIndex++;
                }
            }
        });

        return hash;
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        // Popcount for x64.
        var x = a ^ b;
        var count = 0;
        while (x != 0)
        {
            x &= (x - 1);
            count++;
        }
        return count;
    }
}
