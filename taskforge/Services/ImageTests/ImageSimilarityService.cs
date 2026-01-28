using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

    public ImageSimilarityService(ILogger<ImageSimilarityService> logger)
    {
        _logger = logger;
    }

    public async Task<double> GetSimilarityPercentAsync(
        Stream expected,
        Stream actual,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Decode both images.
            using var expImg = await LoadAsRgbaAsync(expected, ct);
            using var actImg = await LoadAsRgbaAsync(actual, ct);

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ImageSimilarity] Failed to compare images. Returning 0% similarity.");
            return 0;
        }
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
