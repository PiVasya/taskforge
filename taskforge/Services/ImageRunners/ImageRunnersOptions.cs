namespace taskforge.Services.ImageRunners;

public sealed class ImageRunnersOptions
{
    public string? PythonUrl { get; set; }
    public string? PascalUrl { get; set; }

    /// <summary>
    /// Таймаут на запрос к image-runner, мс.
    /// </summary>
    public int TimeoutMs { get; set; } = 15000;
}
