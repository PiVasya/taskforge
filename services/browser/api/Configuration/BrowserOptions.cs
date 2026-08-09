namespace TaskForge.Browser.Api.Configuration;

public sealed class BrowserOptions
{
    public string DefaultSite { get; set; } = "main";
    public Dictionary<string, string> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = "https://taskforge.by",
        ["ct"] = "https://ct.taskforge.by"
    };

    public string AllowedExternalOrigins { get; set; } = "https://s3.taskforge.by";
    public string UserAgent { get; set; } = "TaskForgeBrowserApi/1.2 (+https://taskforge.by/llms.txt)";
    public bool DisableSandbox { get; set; } = true;
    public bool IgnoreHttpsErrors { get; set; }
    public int NavigationTimeoutSeconds { get; set; } = 20;
    public int ActionTimeoutSeconds { get; set; } = 10;
    public int DefaultWaitMilliseconds { get; set; } = 800;
    public int AgentCaptureWaitMilliseconds { get; set; } = 150;
    public int MaxWaitMilliseconds { get; set; } = 15000;
    public int AppReadyTimeoutMilliseconds { get; set; } = 3500;
    public int FontReadyTimeoutMilliseconds { get; set; } = 1500;
    public int CaptureTimeoutSeconds { get; set; } = 75;
    public int CaptureCacheSeconds { get; set; } = 30;
    public int RecommendedCaptureConcurrency { get; set; } = 1;
    public int MaxConcurrentPublicCaptures { get; set; } = 2;
    public int PublicCaptureQueueWaitMilliseconds { get; set; } = 3000;
    public int MinViewportWidth { get; set; } = 320;
    public int MaxViewportWidth { get; set; } = 2560;
    public int MinViewportHeight { get; set; } = 320;
    public int MaxViewportHeight { get; set; } = 1440;
    public int MaxFullPageHeight { get; set; } = 20000;
    public long MaxScreenshotPixels { get; set; } = 24000000;
    public int MaxSnapshotElements { get; set; } = 500;
    public int MaxSnapshotTextCharacters { get; set; } = 64000;
    public int MaxAriaSnapshotCharacters { get; set; } = 64000;
    public int AriaSnapshotDepth { get; set; } = 14;
    public int MaxEventEntries { get; set; } = 160;
    public int MaxConcurrentOperations { get; set; } = 4;
    public int MaxActiveSessions { get; set; } = 8;
    public int MaxAnonymousSessionsPerOwner { get; set; } = 2;
    public int MaxAuthenticatedSessionsPerOwner { get; set; } = 3;
    public int SessionIdleMinutes { get; set; } = 45;
    public int SessionAbsoluteMinutes { get; set; } = 110;
    public int PublicCacheSeconds { get; set; } = 30;
    public int AgentArtifactTtlSeconds { get; set; } = 3600;
    public int MaxCachedArtifactBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxArtifactResponseBytes { get; set; } = 32 * 1024 * 1024;
    public bool ReduceMotion { get; set; } = true;
    public string DeploymentVersion { get; set; } = "develop";

    public IReadOnlyDictionary<string, Uri> GetSites()
    {
        var result = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Sites)
        {
            if (Uri.TryCreate(pair.Value?.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                result[pair.Key.Trim().ToLowerInvariant()] = new Uri(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        return result;
    }

    public IReadOnlyCollection<Uri> GetAllowedExternalOrigins()
    {
        return (AllowedExternalOrigins ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(uri => new Uri(uri!.GetLeftPart(UriPartial.Authority)))
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class BrowserRateLimitOptions
{
    public bool Enabled { get; set; } = true;
    public int MetadataLimit { get; set; } = 120;
    public int MetadataWindowSeconds { get; set; } = 60;
    public int SnapshotLimit { get; set; } = 30;
    public int SnapshotWindowSeconds { get; set; } = 60;
    public int RenderLimit { get; set; } = 10;
    public int RenderWindowSeconds { get; set; } = 60;
    public int SessionCreateLimit { get; set; } = 5;
    public int SessionCreateWindowSeconds { get; set; } = 60;
    public int SessionActionLimit { get; set; } = 120;
    public int SessionActionWindowSeconds { get; set; } = 60;
    public int NetworkMultiplier { get; set; } = 10;
}
