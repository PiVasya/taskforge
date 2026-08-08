global using Microsoft.AspNetCore.Http;

using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Security;

var options = new BrowserOptions
{
    DefaultSite = "main",
    Sites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = "https://taskforge.by",
        ["ct"] = "https://ct.taskforge.by"
    },
    AllowedExternalOrigins = "https://s3.taskforge.by"
};

var policy = new BrowserUrlPolicy(options);

var allowed = new (string? Input, string Expected)[]
{
    (null, "/"),
    ("", "/"),
    ("/", "/"),
    ("courses", "/courses"),
    ("/courses", "/courses"),
    ("/courses?id=123", "/courses?id=123"),
    ("?tab=all", "/?tab=all"),
    ("/#overview", "/#overview"),
    ("/login?returnUrl=https%3A%2F%2Fexample.invalid%2F", "/login?returnUrl=https%3A%2F%2Fexample.invalid%2F")
};

foreach (var pair in allowed)
{
    var normalized = policy.NormalizeRelativePath(pair.Input);
    Assert(normalized == pair.Expected, $"expected '{pair.Input ?? "<null>"}' -> '{pair.Expected}', got '{normalized}'");
    var page = policy.BuildPageUri("main", pair.Input);
    Assert(page.Scheme == "https" && page.Host == "taskforge.by", $"allowed path escaped TaskForge origin: {pair.Input}");
}

var rejected = new[]
{
    "https://evil.example/",
    "http://evil.example/",
    "file:///etc/passwd",
    "javascript:alert(1)",
    "//evil.example/",
    "%2f%2fevil.example/",
    "/%2f%2fevil.example/",
    "/../secret",
    "/%2e%2e/secret",
    "/%252e%252e/secret",
    @"/foo\bar",
    "/api/site/render",
    "/api/site%2frender",
    "/api/browser/sessions",
    "/api%252fbrowser%252fsessions"
};

foreach (var value in rejected)
{
    try
    {
        policy.NormalizeRelativePath(value);
        throw new InvalidOperationException($"expected path to be rejected: {value}");
    }
    catch (BrowserApiException exception) when (exception.StatusCode == StatusCodes.Status400BadRequest)
    {
        // Expected.
    }
}

Assert(policy.IsAllowedRequest(new Uri("https://taskforge.by/assets/app.js")), "main origin must be allowed");
Assert(policy.IsAllowedRequest(new Uri("https://ct.taskforge.by/assets/app.js")), "CT origin must be allowed");
Assert(policy.IsAllowedRequest(new Uri("https://s3.taskforge.by/file.png")), "configured asset origin must be allowed");
Assert(!policy.IsAllowedRequest(new Uri("https://evil.example/file.png")), "unknown origin must be rejected");
Assert(policy.IsBrowserApiEndpoint(new Uri("https://taskforge.by/api/site/render")), "Browser API recursion must be detected");
Assert(policy.IsBrowserApiEndpoint(new Uri("https://taskforge.by/api%2fbrowser%2fsessions")), "encoded Browser API recursion must be detected");
Assert(!policy.IsBrowserApiEndpoint(new Uri("https://taskforge.by/courses")), "ordinary page must not be classified as Browser API");

Console.WriteLine("[browser-url-policy] regression cases ok");
return;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
