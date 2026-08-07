using System.Text.Json.Serialization;

namespace TaskForge.Browser.Api.Contracts;

public sealed record ApiError(string Message, string Code, string? TraceId = null, int? RetryAfterSeconds = null);

public sealed record SiteRouteDto(
    string Site,
    string Path,
    string Title,
    bool RequiresAuthentication,
    string Kind = "page",
    string? RequiredRole = null,
    string? Notes = null);

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
    object Limits);

public sealed record CreateBrowserSessionRequest(
    string? Site,
    string? Path,
    int? Width,
    int? Height,
    bool? ReadOnly,
    int? WaitMs);

public sealed record NavigateBrowserSessionRequest(string? Path, int? WaitMs);
public sealed record ClickBrowserSessionRequest(string? ElementId, int? ClickCount, bool? IncludeSnapshot);
public sealed record FillBrowserSessionRequest(string? ElementId, string? Value, bool? IncludeSnapshot);
public sealed record PressBrowserSessionRequest(string? ElementId, string? Key, bool? IncludeSnapshot);
public sealed record SelectBrowserSessionRequest(string? ElementId, string? Value, bool? IncludeSnapshot);
public sealed record HoverBrowserSessionRequest(string? ElementId, bool? IncludeSnapshot);
public sealed record CheckBrowserSessionRequest(string? ElementId, bool? Checked, bool? IncludeSnapshot);
public sealed record ScrollBrowserSessionRequest(double? DeltaX, double? DeltaY, string? ElementId, bool? IncludeSnapshot);
public sealed record BrowserSessionSnapshotRequest(bool? IncludeText);

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
    object SessionHeader,
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
    public string Site { get; set; } = "main";
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Authenticated { get; set; }
    public string AccountType { get; set; } = "anonymous";
    public bool ReadOnly { get; set; } = true;
    public SnapshotViewport Viewport { get; set; } = new();
    public SnapshotDocument Document { get; set; } = new();
    public string Text { get; set; } = string.Empty;
    public string AriaSnapshot { get; set; } = string.Empty;
    public List<SnapshotHeading> Headings { get; set; } = [];
    public List<SnapshotElement> Elements { get; set; } = [];
    public SnapshotIssues Issues { get; set; } = new();
    public SnapshotPerformance Performance { get; set; } = new();
    public List<BrowserConsoleEntry> Console { get; set; } = [];
    public List<BrowserNetworkEntry> NetworkFailures { get; set; } = [];
    public List<BrowserNetworkEntry> HttpErrors { get; set; } = [];
    public SnapshotTruncation Truncation { get; set; } = new();
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
    public bool Disabled { get; set; }
    public bool Checked { get; set; }
    public bool Selected { get; set; }
    public bool InViewport { get; set; }
    public SnapshotRect Bounds { get; set; } = new();
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

public sealed record RateLimitDecision(bool Allowed, int Limit, int Remaining, int RetryAfterSeconds, string Bucket);

public sealed record BrowserCaller(
    bool IsAuthenticated,
    Guid? UserId,
    string AccountType,
    string? AccessToken,
    string OwnerKey,
    string NetworkKey,
    bool CredentialWasPresented);
