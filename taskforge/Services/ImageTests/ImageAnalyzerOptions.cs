namespace taskforge.Services.ImageTests;

/// <summary>
/// Настройки внешнего контейнера анализа изображений (image-analyzer).
/// </summary>
public sealed class ImageAnalyzerOptions
{
    /// <summary>
    /// Включить внешнюю нейросетевую проверку.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Базовый URL сервиса (например http://image-analyzer:8080).
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Таймаут запросов к analyzer.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 25;
}
