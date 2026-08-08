namespace TaskForge.Browser.Api.Security;

public static class BrowserSessionAccessPolicy
{
    public static bool CanUse(
        bool sessionOwnerIsAuthenticated,
        string sessionOwnerKey,
        bool callerIsAuthenticated,
        string callerOwnerKey)
    {
        if (!sessionOwnerIsAuthenticated)
        {
            return true;
        }

        return callerIsAuthenticated
            && string.Equals(sessionOwnerKey, callerOwnerKey, StringComparison.Ordinal);
    }
}
