using System.Net;

namespace taskforge.Services.ImageRunners;

/// <summary>
/// Когда image-runner (python/pascal) вернул HTTP 4xx/5xx, мы НЕ должны
/// терять тело ответа (там часто есть compile/runtime ошибки).
///
/// Бросаем это исключение, чтобы контроллер мог вернуть понятный RunnerError,
/// а не "Empty image returned".
/// </summary>
public sealed class ImageRunnerHttpException : Exception
{
    public string Language { get; }
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public ImageRunnerHttpException(string language, HttpStatusCode statusCode, string responseBody)
        : base($"Image runner returned {(int)statusCode} for {language}. Body: {Truncate(responseBody)}")
    {
        Language = language;
        StatusCode = statusCode;
        ResponseBody = responseBody ?? string.Empty;
    }

    private static string Truncate(string? s, int max = 1200)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "...";
    }
}
