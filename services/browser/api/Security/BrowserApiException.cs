namespace TaskForge.Browser.Api.Security;

public sealed class BrowserApiException : Exception
{
    public BrowserApiException(int statusCode, string code, string message, int? retryAfterSeconds = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public int? RetryAfterSeconds { get; }
}
