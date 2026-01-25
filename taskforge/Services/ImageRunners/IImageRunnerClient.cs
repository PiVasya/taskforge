namespace taskforge.Services.ImageRunners;

public sealed class ImageRunnerDebugResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;

    public byte[]? PngBytes { get; init; }
}

public interface IImageRunnerClient
{
    Task<byte[]?> RenderAsync(string language, string sourceCode, CancellationToken ct = default);

    /// <summary>
    /// Renders and returns stdout/stderr + png (if produced).
    /// Some runners may not support this and will fall back to just RenderAsync.
    /// </summary>
    Task<ImageRunnerDebugResult> RenderDebugAsync(string language, string sourceCode, CancellationToken ct = default);
}
