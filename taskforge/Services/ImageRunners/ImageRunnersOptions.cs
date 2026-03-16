namespace taskforge.Services.ImageRunners;

public sealed class ImageRunnersOptions
{
    public string? PythonUrl { get; set; }
    public string? PascalUrl { get; set; }
    public string? CppUrl { get; set; }

    /// <summary>
    /// Таймаут на запрос к image-runner, мс.
    /// </summary>
    // Pascal-рендер может занимать дольше (компиляция + запуск GUI + Enter + скриншот).
    // В проде это значение можно переопределить через конфиг/ENV.
    public int TimeoutMs { get; set; } = 45000;
}
