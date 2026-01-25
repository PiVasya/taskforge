namespace taskforge.Services.ImageTests;

public interface IImageSimilarityService
{
    /// <summary>
    /// Возвращает процент похожести 0..100.
    /// </summary>
    Task<double> GetSimilarityPercentAsync(Stream expected, Stream actual, CancellationToken ct = default);
}
