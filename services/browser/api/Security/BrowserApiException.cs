namespace TaskForge.Browser.Api.Security;

public sealed class BrowserApiException : Exception
{
    public BrowserApiException(
        int statusCode,
        string code,
        string message,
        int? retryAfterSeconds = null,
        object? details = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
        Details = details;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public int? RetryAfterSeconds { get; }
    public object? Details { get; }
}
