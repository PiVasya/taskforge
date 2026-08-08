using TaskForge.Browser.Api.Security;

var cases = new[]
{
    new Case("anonymous same network", false, "anonymous:network-a", false, "anonymous:network-a", true),
    new Case("anonymous changed network", false, "anonymous:network-a", false, "anonymous:network-b", true),
    new Case("anonymous token used while caller is authenticated", false, "anonymous:network-a", true, "user:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true),
    new Case("authenticated same user", true, "user:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, "user:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true),
    new Case("authenticated different user", true, "user:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, "user:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false),
    new Case("authenticated session without access token", true, "user:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, "anonymous:network-a", false),
};

foreach (var test in cases)
{
    var actual = BrowserSessionAccessPolicy.CanUse(
        test.SessionOwnerIsAuthenticated,
        test.SessionOwnerKey,
        test.CallerIsAuthenticated,
        test.CallerOwnerKey);

    if (actual != test.Expected)
    {
        Console.Error.WriteLine($"[browser-session-access] FAILED {test.Name}: expected={test.Expected} actual={actual}");
        return 1;
    }
}

Console.WriteLine($"[browser-session-access] regression cases ok ({cases.Length})");
return 0;

internal sealed record Case(
    string Name,
    bool SessionOwnerIsAuthenticated,
    string SessionOwnerKey,
    bool CallerIsAuthenticated,
    string CallerOwnerKey,
    bool Expected);
