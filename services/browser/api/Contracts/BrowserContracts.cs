using System.ComponentModel.DataAnnotations;

namespace TaskForge.Browser.Api.Contracts;

public sealed record ApiError(
    string Message,
    string Code,
    string? TraceId = null,
    int? RetryAfterSeconds = null,
    object? Details = null);

public sealed record SiteRouteDto(
    string Site,
    string Path,
    string Title,
    bool RequiresAuthentication,
    string Kind = "page",
    string? RequiredRole = null,
    string? Notes = null);

public sealed record SiteRoutesResponse(IReadOnlyList<SiteRouteDto> Routes);

public sealed record SiteViewportLimits(
    int MinWidth,
    int MaxWidth,
    int MinHeight,
    int MaxHeight);

public sealed record SiteApiLimits(
    SiteViewportLimits Viewport,
    int MaxFullPageHeight,
    long MaxScreenshotPixels,
    int MaxArtifactResponseBytes,
    int MaxSnapshotElements,
    int MaxSnapshotTextCharacters,
    int MaxAriaSnapshotCharacters,
    int ActiveSessions,
    int AnonymousSessionsPerOwner,
    int AuthenticatedSessionsPerOwner,
    int SessionIdleMinutes,
    int SessionAbsoluteMinutes,
    int AgentArtifactTtlSeconds,
    int CaptureTimeoutSeconds,
    int CaptureCacheSeconds,
    int RecommendedCaptureConcurrency,
    int AuthenticatedAiRateLimitMultiplier,
    string SemanticSnapshotVersion,
    IReadOnlyList<string> RateLimitHeaders);

public sealed record SiteInfoResponse(
    string Name,
    string ApiVersion,
    string DefaultSite,
    IReadOnlyDictionary<string, string> Sites,
    bool AnonymousBrowsing,
    bool AiAccounts,
    bool InteractiveBrowserSessions,
    string Discovery,
    string Instructions,
    string OpenApi,
    string Registration,
    string Login,
    string Snapshot,
    string Render,
    string BrowserSessions,
    SiteApiLimits Limits);

public sealed record CreateBrowserSessionRequest(
    [property: StringLength(32)] string? Site,
    [property: StringLength(2048)] string? Path,
    [property: Range(320, 2560)] int? Width,
    [property: Range(320, 1440)] int? Height,
    bool? ReadOnly,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record NavigateBrowserSessionRequest(
    [property: StringLength(2048)] string? Path,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record ClickBrowserSessionRequest(
    [property: Required, RegularExpression("^(?:tf[1-9][0-9]{0,5}|[A-Za-z0-9][A-Za-z0-9_.:-]{0,159})$")] string? ElementId,
    [property: Range(1, 2)] int? ClickCount,
    bool? IncludeSnapshot,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record FillBrowserSessionRequest(
    [property: Required, RegularExpression("^(?:tf[1-9][0-9]{0,5}|[A-Za-z0-9][A-Za-z0-9_.:-]{0,159})$")] string? ElementId,
    [property: StringLength(20000)] string? Value,
    bool? IncludeSnapshot,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record PressBrowserSessionRequest(
    [property: Required, RegularExpression("^(?:tf[1-9][0-9]{0,5}|[A-Za-z0-9][A-Za-z0-9_.:-]{0,159})$")] string? ElementId,
    [property: Required, StringLength(64, MinimumLength = 1)] string? Key,
    bool? IncludeSnapshot,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record SelectBrowserSessionRequest(
    [property: Required, RegularExpression("^(?:tf[1-9][0-9]{0,5}|[A-Za-z0-9][A-Za-z0-9_.:-]{0,159})$")] string? ElementId,
    [property: Required, StringLength(1000)] string? Value,
    bool? IncludeSnapshot,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record HoverBrowserSessionRequest(
    [property: Required, RegularExpression("^(?:tf[1-9][0-9]{0,5}|[A-Za-z0-9][A-Za-z0-9_.:-]{0,159})$")] string? ElementId,
    bool? IncludeSnapshot,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record CheckBrowserSessionRequest(
    [property: Required, RegularExpression("^(?:tf[1-9][0-9]{0,5}|[A-Za-z0-9][A-Za-z0-9_.:-]{0,159})$")] string? ElementId,
    bool? Checked,
    bool? IncludeSnapshot,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record ScrollBrowserSessionRequest(
    [property: Range(-5000d, 5000d)] double? DeltaX,
    [property: Range(-5000d, 5000d)] double? DeltaY,
    [property: RegularExpression("^(?:tf[1-9][0-9]{0,5}|[A-Za-z0-9][A-Za-z0-9_.:-]{0,159})$")] string? ElementId,
    bool? IncludeSnapshot,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record BrowserSessionSnapshotRequest(bool? IncludeText);

public sealed record BrowserSessionHeader(string Name, string Value);

public sealed record CreateBrowserSessionResponse(
    Guid Id,
    string SessionToken,
    string Site,
    string Url,
    bool ReadOnly,
    bool Authenticated,
    string AccountType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int Width,
    int Height,
    string SnapshotUrl,
    string ScreenshotUrl,
    BrowserSessionHeader SessionHeader,
    SiteSnapshotResponse Snapshot);

public sealed record BrowserActionResponse(
    Guid SessionId,
    string Action,
    string Url,
    string Title,
    DateTimeOffset AtUtc,
    SiteSnapshotResponse? Snapshot = null);

public sealed class SiteSnapshotResponse
{
    public string SemanticSnapshotVersion { get; set; } = "2.2";
    public string Site { get; set; } = "main";
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Authenticated { get; set; }
    public string AccountType { get; set; } = "anonymous";
    public bool ReadOnly { get; set; } = true;
    public string CaptureMode { get; set; } = "anonymous-read-only";
    public bool PolicyInterference { get; set; }
    public string PageReadyState { get; set; } = string.Empty;
    public bool AppReady { get; set; }
    public SnapshotReadiness Readiness { get; set; } = new();
    public SnapshotViewport Viewport { get; set; } = new();
    public SnapshotDocument Document { get; set; } = new();
    public string Text { get; set; } = string.Empty;
    public string AriaSnapshot { get; set; } = string.Empty;
    public List<SnapshotHeading> Headings { get; set; } = [];
    public List<SnapshotElement> Elements { get; set; } = [];
    public List<SnapshotDiscoveredLink> DiscoveredLinks { get; set; } = [];
    public SnapshotIssues Issues { get; set; } = new();
    public SnapshotPerformance Performance { get; set; } = new();
    public List<BrowserConsoleEntry> Console { get; set; } = [];
    public List<BrowserNetworkEntry> NetworkFailures { get; set; } = [];
    public List<BrowserNetworkEntry> HttpErrors { get; set; } = [];
    public List<BrowserPolicyBlockedEntry> PolicyBlockedRequests { get; set; } = [];
    public SnapshotTruncation Truncation { get; set; } = new();
}

public sealed class SnapshotReadiness
{
    public string Stage { get; set; } = "unknown";
    public string PageReadyState { get; set; } = string.Empty;
    public bool AppReadyMarkerPresent { get; set; }
    public bool AppReady { get; set; }
    public bool RootMounted { get; set; }
    public bool TimedOut { get; set; }
    public int StabilizationMilliseconds { get; set; }
    public int PendingRequestCount { get; set; }
    public List<string> PendingRequests { get; set; } = [];
}

public sealed class SnapshotViewport
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double DevicePixelRatio { get; set; } = 1;
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
}

public sealed class SnapshotDocument
{
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HorizontalOverflow { get; set; }
    public string Language { get; set; } = string.Empty;
    public int InteractiveElementCount { get; set; }
    public int ImageCount { get; set; }
}

public sealed class SnapshotHeading
{
    public int Level { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class SnapshotElement
{
    public string Id { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Href { get; set; }
    public string? Placeholder { get; set; }
    public string? TestId { get; set; }
    public string? AutomationId { get; set; }
    public string? AutomationRole { get; set; }
    public string? AutomationAction { get; set; }
    public string? AutomationState { get; set; }
    public string? AutomationKind { get; set; }
    public int? QuestionIndex { get; set; }
    public string? QuestionId { get; set; }
    public int? AnswerOptionIndex { get; set; }
    public string? AnswerOptionKey { get; set; }
    public string? Value { get; set; }
    public List<SnapshotSelectOption> Options { get; set; } = [];
    public bool Interactive { get; set; }
    public bool Disabled { get; set; }
    public bool Checked { get; set; }
    public bool Selected { get; set; }
    public bool InViewport { get; set; }
    public bool Clipped { get; set; }
    public double VisibleRatio { get; set; }
    public SnapshotRect Bounds { get; set; } = new();
    public SnapshotRect VisibleBounds { get; set; } = new();
}

public sealed class SnapshotSelectOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Selected { get; set; }
    public bool Disabled { get; set; }
}

public sealed class SnapshotDiscoveredLink
{
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string CaptureCurrentViewport { get; set; } = string.Empty;
    public string CaptureMobile { get; set; } = string.Empty;
    public string CaptureDesktop { get; set; } = string.Empty;
}

public sealed class SnapshotRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class SnapshotPerformance
{
    public double? DomContentLoadedMilliseconds { get; set; }
    public double? LoadMilliseconds { get; set; }
    public double? FirstContentfulPaintMilliseconds { get; set; }
    public double? LargestContentfulPaintMilliseconds { get; set; }
    public double? CumulativeLayoutShift { get; set; }
    public int ResourceCount { get; set; }
    public long TransferSizeBytes { get; set; }
    public int LongTaskCount { get; set; }
    public double LongTaskDurationMilliseconds { get; set; }
}

public sealed class SnapshotIssues
{
    public int SmallTouchTargetCount { get; set; }
    public List<string> SmallTouchTargetElementIds { get; set; } = [];
    public int TouchTargetBelow44Count { get; set; }
    public List<string> TouchTargetBelow44ElementIds { get; set; } = [];
    public int UnlabelledInteractiveCount { get; set; }
    public List<string> UnlabelledInteractiveElementIds { get; set; } = [];
    public int ImagesWithoutAltCount { get; set; }
    public int DuplicateIdCount { get; set; }
    public int H1Count { get; set; }
    public int HeadingLevelSkipCount { get; set; }
    public bool MissingDocumentLanguage { get; set; }
    public List<SnapshotOverflowElement> OverflowElements { get; set; } = [];
}

public sealed class SnapshotOverflowElement
{
    public string Tag { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public SnapshotRect Bounds { get; set; } = new();
}

public sealed class SnapshotTruncation
{
    public bool TextTruncated { get; set; }
    public bool ElementsTruncated { get; set; }
    public bool AriaSnapshotTruncated { get; set; }
    public int TotalInteractiveElements { get; set; }
}

public sealed record BrowserConsoleEntry(string Type, string Text, DateTimeOffset AtUtc);
public sealed record BrowserNetworkEntry(string Method, string Url, string ResourceType, int? Status, string? Failure, DateTimeOffset AtUtc);
public sealed record BrowserPolicyBlockedEntry(
    string Method,
    string Url,
    string ResourceType,
    string Reason,
    bool Expected,
    DateTimeOffset AtUtc);

public sealed record RateLimitDecision(
    bool Allowed,
    int Limit,
    int Remaining,
    int RetryAfterSeconds,
    string Bucket,
    int WindowSeconds,
    DateTimeOffset ResetAtUtc);

public sealed record BrowserCaller(
    bool IsAuthenticated,
    Guid? UserId,
    string AccountType,
    string? AccessToken,
    string OwnerKey,
    string NetworkKey,
    bool CredentialWasPresented);
