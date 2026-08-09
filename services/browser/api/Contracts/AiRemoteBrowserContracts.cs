using System.ComponentModel.DataAnnotations;

namespace TaskForge.Browser.Api.Contracts;

public sealed record AiRemoteStartRequest(
    [property: StringLength(32)] string? Site,
    [property: StringLength(2048)] string? Path,
    [property: Range(320, 2560)] int? Width,
    [property: Range(320, 1440)] int? Height,
    [property: Range(0, 15000)] int? WaitMs);

public sealed record AiRemoteActionRequest(
    [property: StringLength(2048)] string? Path,
    [property: StringLength(400000)] string? PathBase64Url,
    [property: StringLength(160)] string? Element,
    [property: StringLength(20000)] string? Value,
    [property: StringLength(400000)] string? ValueBase64Url,
    [property: StringLength(1000)] string? SelectValue,
    [property: StringLength(64)] string? Key,
    bool? Checked,
    [property: Range(1, 2)] int? ClickCount,
    [property: Range(-5000d, 5000d)] double? DeltaX,
    [property: Range(-5000d, 5000d)] double? DeltaY,
    [property: Range(0d, 2560d)] double? X,
    [property: Range(0d, 1440d)] double? Y,
    [property: Range(0, 15000)] int? WaitMs,
    bool? IncludeSnapshot);

public sealed record AiRemoteStartResponse(
    string ApiVersion,
    string Message,
    DateTimeOffset ExpiresAtUtc,
    string ConfirmUrl,
    string ConfirmHtmlUrl,
    object Notes);

public sealed record AiRemoteBrowserLinks(
    string Home,
    string Snapshot,
    string Screenshot,
    string FullPageScreenshot,
    string AnnotatedScreenshot,
    string AnnotatedFullPageScreenshot,
    string View,
    string FullPageView,
    string AnnotatedView,
    string AnnotatedFullPageView,
    string NavigateTemplate,
    string ClickTemplate,
    string FillTemplate,
    string InsertTemplate,
    string SelectTemplate,
    string PressTemplate,
    string KeyTemplate,
    string HoverTemplate,
    string MouseClickTemplate,
    string CheckTemplate,
    string ScrollTemplate,
    string WaitTemplate,
    string Back,
    string Reload,
    string Close);

public sealed record AiRemoteBrowserSessionResponse(
    string ApiVersion,
    Guid SessionId,
    string SessionSecret,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    string Site,
    int Width,
    int Height,
    AiRemoteBrowserLinks Links,
    SiteSnapshotResponse Snapshot);

public sealed record AiRemoteBrowserActionResponse(
    string ApiVersion,
    Guid SessionId,
    string Action,
    DateTimeOffset AtUtc,
    AiRemoteBrowserLinks Links,
    SiteSnapshotResponse? Snapshot,
    string? Message = null);

internal sealed record AiRemoteStartChallenge(
    string Site,
    string Path,
    int Width,
    int Height,
    int WaitMs,
    string Provider,
    string NetworkKey,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed class AiRemoteBrowserSession
{
    public Guid Id { get; init; }
    public required string SecretHash { get; init; }
    public required string Provider { get; init; }
    public required string NetworkKey { get; init; }
    public required BrowserCaller Caller { get; init; }
    public required Guid BrowserSessionId { get; init; }
    public required string BrowserSessionToken { get; init; }
    public required string Site { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset AbsoluteExpiresAtUtc { get; init; }
    public required TimeSpan IdleTimeout { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public SemaphoreSlim Mutex { get; } = new(1, 1);

    public bool IsExpired(DateTimeOffset now)
        => now >= AbsoluteExpiresAtUtc || now - LastSeenAtUtc >= IdleTimeout;
}
