using System;

namespace taskforge.Services.ImageTests;

/// <summary>
/// Thrown when external image-analyzer service is enabled but cannot be reached / fails.
/// API should translate this into HTTP 503 so the client can show "temporarily unavailable".
/// </summary>
public sealed class ImageAnalyzerUnavailableException : Exception
{
    public ImageAnalyzerUnavailableException(string message) : base(message) { }

    public ImageAnalyzerUnavailableException(string message, Exception inner) : base(message, inner) { }
}
