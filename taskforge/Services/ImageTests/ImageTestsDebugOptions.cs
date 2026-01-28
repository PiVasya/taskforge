namespace taskforge.Services.ImageTests;

/// <summary>
/// Настройки диагностического логирования для image-test.
///
/// Важно:
/// - Логи пишутся в stdout через Console.WriteLine (удобно в Docker / Kubernetes).
/// - В проде можно отключить VerboseConsole.
/// </summary>
public sealed class ImageTestsDebugOptions
{
    /// <summary>
    /// Включить подробные Console.WriteLine логи для пайплайна сравнения.
    /// </summary>
    public bool VerboseConsole { get; set; } = true;

    /// <summary>
    /// Если true, сервис будет сохранять промежуточные артефакты (маски/warp)
    /// в директорию DumpDir (для ручной диагностики).
    /// </summary>
    public bool DumpIntermediate { get; set; } = false;

    /// <summary>
    /// Директория для дампов (в контейнере по умолчанию /tmp/image-tests).
    /// </summary>
    public string DumpDir { get; set; } = "/tmp/image-tests";
}
