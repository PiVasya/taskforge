using System.Threading;
using System.Threading.Tasks;

namespace taskforge.Services.ImageTests;

public interface IImageAnalyzerClient
{
    /// <summary>
    /// Сравнение двух изображений через внешний сервис (OpenCLIP).
    /// Возвращает null, если сервис не отвечает/не сконфигурирован.
    /// </summary>
    Task<ImageAnalyzerCompareResult?> CompareAsync(byte[] expected, byte[] actual, CancellationToken ct = default);
}
