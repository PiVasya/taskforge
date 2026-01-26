using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace taskforge.Services.ImageTests;

/// <summary>
/// Быстрое сравнение изображений.
/// Алгоритм: приводим оба изображения к 128x128 (letterbox не делаем, просто resize)
/// и считаем среднюю абсолютную ошибку (MAE) по каналам.
/// </summary>
public sealed class ImageSimilarityService : IImageSimilarityService
{
    public async Task<double> GetSimilarityPercentAsync(Stream expected, Stream actual, CancellationToken ct = default)
    {
        if (expected == null) throw new ArgumentNullException(nameof(expected));
        if (actual == null) throw new ArgumentNullException(nameof(actual));

        if (expected.CanSeek) expected.Position = 0;
        if (actual.CanSeek) actual.Position = 0;

        using var img1 = await Image.LoadAsync<Rgba32>(expected, ct);
        using var img2 = await Image.LoadAsync<Rgba32>(actual, ct);

        const int size = 128;
        img1.Mutate(x => x.Resize(size, size));
        img2.Mutate(x => x.Resize(size, size));

        // Самый совместимый способ (и для ImageSharp 2.x, и для 3.x):
        // копируем пиксели в обычные массивы и сравниваем их.
        var a = new Rgba32[size * size];
        var b = new Rgba32[size * size];

        img1.CopyPixelDataTo(a);
        img2.CopyPixelDataTo(b);

        double sum = 0.0;
        int pixels = size * size;

        for (int i = 0; i < pixels; i++)
        {
            var p = a[i];
            var q = b[i];

            sum += Math.Abs(p.R - q.R);
            sum += Math.Abs(p.G - q.G);
            sum += Math.Abs(p.B - q.B);
            // альфу тоже учитываем, но слабее
            sum += 0.5 * Math.Abs(p.A - q.A);
        }

        // Нормализация:
        // максимум на пиксель = 255*(R+G+B) + 0.5*255*(A) = 255*3.5
        double max = pixels * (255.0 * 3.5);
        double mae = sum / max;              // 0..1
        double similarity = (1.0 - mae) * 100.0;

        if (similarity < 0) similarity = 0;
        if (similarity > 100) similarity = 100;

        return similarity;
    }
}
