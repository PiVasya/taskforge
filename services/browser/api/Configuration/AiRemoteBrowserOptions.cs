namespace TaskForge.Browser.Api.Configuration;

public sealed class AiRemoteBrowserOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowGetMutations { get; set; } = true;
    public string DefaultSite { get; set; } = "main";
    public string DefaultPath { get; set; } = "/";
    public int DefaultWidth { get; set; } = 1440;
    public int DefaultHeight { get; set; } = 900;
    public int DefaultWaitMilliseconds { get; set; } = 1000;
    public int StartChallengeTtlSeconds { get; set; } = 300;
    public int SessionIdleMinutes { get; set; } = 45;
    public int SessionAbsoluteMinutes { get; set; } = 105;
    public int MaxSessionsPerNetwork { get; set; } = 2;
    public int MaxValueCharacters { get; set; } = 20000;
    public int StartLimit { get; set; } = 30;
    public int StartWindowSeconds { get; set; } = 60;
    public int ConfirmLimit { get; set; } = 5;
    public int ConfirmWindowSeconds { get; set; } = 60;
    public int ActionLimit { get; set; } = 240;
    public int ActionWindowSeconds { get; set; } = 60;
    public int ScreenshotLimit { get; set; } = 30;
    public int ScreenshotWindowSeconds { get; set; } = 60;
}
