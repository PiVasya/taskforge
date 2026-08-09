using System.Text;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

internal sealed class AiRemoteBrowserService(
    AiRemoteBrowserSessionStore store,
    BrowserSessionRegistry browserSessions,
    BrowserUrlPolicy urlPolicy,
    BrowserOptions browserOptions,
    AiRemoteBrowserOptions options,
    ILogger<AiRemoteBrowserService> logger)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly AiRemoteBrowserSessionStore _store = store;
    private readonly BrowserSessionRegistry _browserSessions = browserSessions;
    private readonly BrowserUrlPolicy _urlPolicy = urlPolicy;
    private readonly BrowserOptions _browserOptions = browserOptions;
    private readonly AiRemoteBrowserOptions _options = options;
    private readonly ILogger<AiRemoteBrowserService> _logger = logger;

    public (string Challenge, AiRemoteStartChallenge Record) CreateChallenge(
        BrowserCaller networkCaller,
        string? site,
        string? path,
        int? width,
        int? height,
        int? waitMs,
        string provider)
    {
        EnsureEnabled();
        var normalizedSite = string.IsNullOrWhiteSpace(site) ? _options.DefaultSite : site.Trim().ToLowerInvariant();
        _ = _urlPolicy.ResolveSite(normalizedSite);
        var normalizedPath = _urlPolicy.NormalizeRelativePath(string.IsNullOrWhiteSpace(path) ? _options.DefaultPath : path);
        var w = width ?? _options.DefaultWidth;
        var h = height ?? _options.DefaultHeight;
        if (w < _browserOptions.MinViewportWidth || w > _browserOptions.MaxViewportWidth
            || h < _browserOptions.MinViewportHeight || h > _browserOptions.MaxViewportHeight)
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_VIEWPORT", "Некорректный viewport AI remote-browser.");
        }
        var wait = NormalizeWait(waitMs);
        return _store.CreateChallenge(normalizedSite, normalizedPath, w, h, wait, provider, networkCaller.NetworkKey);
    }

    public async Task<AiRemoteStartedSession> ConfirmAsync(
        string? challenge,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var start = _store.ConsumeChallenge(challenge);
        _store.EnsureNetworkCapacity(start.NetworkKey);
        var ownerKey = $"ai-remote:{Guid.NewGuid():N}";
        var caller = new BrowserCaller(
            IsAuthenticated: false,
            UserId: null,
            AccountType: "anonymous",
            AccessToken: null,
            OwnerKey: ownerKey,
            NetworkKey: start.NetworkKey,
            CredentialWasPresented: false);

        var created = await _browserSessions.CreateAgentInteractiveAsync(
            new CreateBrowserSessionRequest(start.Site, start.Path, start.Width, start.Height, ReadOnly: false, WaitMs: start.WaitMs),
            caller,
            cancellationToken);

        try
        {
            var (session, secret) = _store.AddSession(created.Session.Id, created.RawToken, caller, start);
            return new AiRemoteStartedSession(session, secret, created.Snapshot);
        }
        catch
        {
            try { await _browserSessions.CloseAsync(created.Session.Id, created.RawToken, caller); }
            catch { /* preserve original failure */ }
            throw;
        }
    }

    public async Task<AiRemoteSnapshotOutcome> SnapshotAsync(
        Guid id,
        string? secret,
        bool includeText,
        int? waitMs,
        CancellationToken cancellationToken)
    {
        var session = _store.Resolve(id, secret);
        await session.Mutex.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _browserSessions.SnapshotAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                includeText,
                NormalizeWait(waitMs, 0),
                cancellationToken);
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AiRemoteSnapshotOutcome(session, snapshot);
        }
        finally { session.Mutex.Release(); }
    }

    public async Task<AiRemoteScreenshotOutcome> ScreenshotAsync(
        Guid id,
        string? secret,
        bool fullPage,
        bool annotated,
        CancellationToken cancellationToken)
    {
        var session = _store.Resolve(id, secret);
        await session.Mutex.WaitAsync(cancellationToken);
        try
        {
            var screenshot = await _browserSessions.ScreenshotAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                fullPage,
                annotated,
                cancellationToken);
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AiRemoteScreenshotOutcome(session, screenshot);
        }
        finally { session.Mutex.Release(); }
    }

    public Task<AiRemoteActionOutcome> NavigateAsync(Guid id, string? secret, string? path, string? pathBase64Url, int? waitMs, CancellationToken cancellationToken)
    {
        var resolved = ResolveText(path, pathBase64Url, 2048, "AI_REMOTE_PATH_TOO_LONG");
        resolved = _urlPolicy.NormalizeRelativePath(resolved);
        return ActionAsync(id, secret, "navigate", async session =>
            await _browserSessions.NavigateAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new NavigateBrowserSessionRequest(resolved, NormalizeWait(waitMs)),
                includeSnapshot: true,
                cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<AiRemoteActionOutcome> ClickAsync(Guid id, string? secret, string? element, int? clickCount, int? waitMs, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "click", async session =>
            await _browserSessions.ClickAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new ClickBrowserSessionRequest(RequireElement(element), Math.Clamp(clickCount ?? 1, 1, 2), IncludeSnapshot: true, WaitMs: NormalizeWait(waitMs)),
                cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> FillAsync(Guid id, string? secret, string? element, string? value, string? valueBase64Url, int? waitMs, CancellationToken cancellationToken)
    {
        var resolved = ResolveText(value, valueBase64Url, _options.MaxValueCharacters, "AI_REMOTE_VALUE_TOO_LONG");
        return ActionAsync(id, secret, "fill", async session =>
            await _browserSessions.FillAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new FillBrowserSessionRequest(RequireElement(element), resolved, IncludeSnapshot: true, WaitMs: NormalizeWait(waitMs)),
                cancellationToken), cancellationToken);
    }

    public Task<AiRemoteActionOutcome> InsertAsync(Guid id, string? secret, string? element, string? value, string? valueBase64Url, int? waitMs, CancellationToken cancellationToken)
    {
        // Keep GET URLs comfortably below common proxy request-line limits. Longer
        // text is sent as several insert chunks; POST clients can keep using fill.
        var resolved = ResolveText(value, valueBase64Url, Math.Min(_options.MaxValueCharacters, 4000), "AI_REMOTE_INSERT_TOO_LONG");
        return ActionAsync(id, secret, "insert", async session =>
            await _browserSessions.InsertTextAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                RequireElement(element),
                resolved,
                NormalizeWait(waitMs),
                cancellationToken), cancellationToken);
    }

    public Task<AiRemoteActionOutcome> SelectAsync(Guid id, string? secret, string? element, string? value, int? waitMs, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "select", async session =>
            await _browserSessions.SelectAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new SelectBrowserSessionRequest(RequireElement(element), value ?? string.Empty, IncludeSnapshot: true, WaitMs: NormalizeWait(waitMs)),
                cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> HoverAsync(Guid id, string? secret, string? element, int? waitMs, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "hover", async session =>
            await _browserSessions.HoverAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new HoverBrowserSessionRequest(RequireElement(element), IncludeSnapshot: true, WaitMs: NormalizeWait(waitMs)),
                cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> KeyAsync(Guid id, string? secret, string? key, int? waitMs, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "key", async session =>
            await _browserSessions.KeyboardPressAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                key ?? string.Empty,
                NormalizeWait(waitMs),
                cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> MouseClickAsync(Guid id, string? secret, double? x, double? y, int? clickCount, int? waitMs, CancellationToken cancellationToken)
    {
        if (x is null || y is null)
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "AI_REMOTE_MOUSE_COORDINATES_REQUIRED", "Укажите x и y внутри текущего viewport.");
        }
        return ActionAsync(id, secret, "mouse-click", async session =>
            await _browserSessions.MouseClickAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                x.Value,
                y.Value,
                Math.Clamp(clickCount ?? 1, 1, 2),
                NormalizeWait(waitMs),
                cancellationToken), cancellationToken);
    }

    public Task<AiRemoteActionOutcome> PressAsync(Guid id, string? secret, string? element, string? key, int? waitMs, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "press", async session =>
            await _browserSessions.PressAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new PressBrowserSessionRequest(RequireElement(element), key, IncludeSnapshot: true, WaitMs: NormalizeWait(waitMs)),
                cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> CheckAsync(Guid id, string? secret, string? element, bool? isChecked, int? waitMs, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "check", async session =>
            await _browserSessions.CheckAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new CheckBrowserSessionRequest(RequireElement(element), isChecked ?? true, IncludeSnapshot: true, WaitMs: NormalizeWait(waitMs)),
                cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> ScrollAsync(Guid id, string? secret, string? element, double? deltaX, double? deltaY, int? waitMs, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "scroll", async session =>
            await _browserSessions.ScrollAsync(
                session.BrowserSessionId,
                session.BrowserSessionToken,
                session.Caller,
                new ScrollBrowserSessionRequest(deltaX, deltaY, string.IsNullOrWhiteSpace(element) ? null : RequireElement(element), IncludeSnapshot: true, WaitMs: NormalizeWait(waitMs)),
                cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> BackAsync(Guid id, string? secret, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "back", async session =>
            await _browserSessions.BackAsync(session.BrowserSessionId, session.BrowserSessionToken, session.Caller, includeSnapshot: true, cancellationToken: cancellationToken), cancellationToken);

    public Task<AiRemoteActionOutcome> ReloadAsync(Guid id, string? secret, CancellationToken cancellationToken)
        => ActionAsync(id, secret, "reload", async session =>
            await _browserSessions.ReloadAsync(session.BrowserSessionId, session.BrowserSessionToken, session.Caller, includeSnapshot: true, cancellationToken: cancellationToken), cancellationToken);

    public async Task<AiRemoteActionOutcome> PerformAsync(
        Guid id,
        string? secret,
        string? action,
        AiRemoteActionRequest request,
        CancellationToken cancellationToken)
    {
        return (action ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "navigate" => await NavigateAsync(id, secret, request.Path, request.PathBase64Url, request.WaitMs, cancellationToken),
            "click" => await ClickAsync(id, secret, request.Element, request.ClickCount, request.WaitMs, cancellationToken),
            "fill" => await FillAsync(id, secret, request.Element, request.Value, request.ValueBase64Url, request.WaitMs, cancellationToken),
            "insert" => await InsertAsync(id, secret, request.Element, request.Value, request.ValueBase64Url, request.WaitMs, cancellationToken),
            "select" => await SelectAsync(id, secret, request.Element, request.SelectValue ?? request.Value, request.WaitMs, cancellationToken),
            "press" => await PressAsync(id, secret, request.Element, request.Key, request.WaitMs, cancellationToken),
            "key" => await KeyAsync(id, secret, request.Key, request.WaitMs, cancellationToken),
            "hover" => await HoverAsync(id, secret, request.Element, request.WaitMs, cancellationToken),
            "mouse-click" => await MouseClickAsync(id, secret, request.X, request.Y, request.ClickCount, request.WaitMs, cancellationToken),
            "check" => await CheckAsync(id, secret, request.Element, request.Checked, request.WaitMs, cancellationToken),
            "scroll" => await ScrollAsync(id, secret, request.Element, request.DeltaX, request.DeltaY, request.WaitMs, cancellationToken),
            "back" => await BackAsync(id, secret, cancellationToken),
            "reload" => await ReloadAsync(id, secret, cancellationToken),
            _ => throw new BrowserApiException(StatusCodes.Status400BadRequest, "AI_REMOTE_ACTION_UNKNOWN", "Доступны navigate, click, fill, insert, select, press, key, hover, mouse-click, check, scroll, back, reload.")
        };
    }

    public async Task CloseAsync(Guid id, string? secret)
    {
        var session = _store.Resolve(id, secret, touch: false);
        if (!_store.TryRemove(id, out _)) return;
        try
        {
            await _browserSessions.CloseAsync(session.BrowserSessionId, session.BrowserSessionToken, session.Caller);
        }
        catch (BrowserApiException ex) when (ex.Code is "SESSION_NOT_FOUND" or "SESSION_CLOSED" or "SESSION_EXPIRED")
        {
        }
    }

    public async Task CloseExpiredAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _store.TakeExpired())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _browserSessions.CloseAsync(session.BrowserSessionId, session.BrowserSessionToken, session.Caller);
            }
            catch (BrowserApiException ex) when (ex.Code is "SESSION_NOT_FOUND" or "SESSION_CLOSED" or "SESSION_EXPIRED")
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to close expired AI remote-browser session {SessionId}.", session.Id);
            }
        }
    }

    private async Task<AiRemoteActionOutcome> ActionAsync(
        Guid id,
        string? secret,
        string expectedAction,
        Func<AiRemoteBrowserSession, Task<BrowserActionResponse>> action,
        CancellationToken cancellationToken)
    {
        var session = _store.Resolve(id, secret);
        await session.Mutex.WaitAsync(cancellationToken);
        try
        {
            var result = await action(session);
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AiRemoteActionOutcome(
                session,
                string.IsNullOrWhiteSpace(result.Action) ? expectedAction : result.Action,
                result.Snapshot,
                null);
        }
        finally { session.Mutex.Release(); }
    }

    private int NormalizeWait(int? waitMs, int? fallback = null)
        => Math.Clamp(waitMs ?? fallback ?? _options.DefaultWaitMilliseconds, 0, _browserOptions.MaxWaitMilliseconds);

    private static string RequireElement(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.Length == 0)
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "AI_REMOTE_ELEMENT_REQUIRED", "Укажите tfN или стабильный automationId из snapshot.");
        }
        return result;
    }

    private static string ResolveText(string? plain, string? base64Url, int maxCharacters, string tooLongCode)
    {
        string value;
        if (!string.IsNullOrWhiteSpace(base64Url))
        {
            try
            {
                var normalized = base64Url.Replace('-', '+').Replace('_', '/');
                normalized += normalized.Length % 4 switch { 0 => string.Empty, 2 => "==", 3 => "=", _ => throw new FormatException() };
                value = StrictUtf8.GetString(Convert.FromBase64String(normalized));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                throw new BrowserApiException(StatusCodes.Status400BadRequest, "AI_REMOTE_BASE64URL_INVALID", "Значение valueBase64Url имеет неверный формат.");
            }
        }
        else value = plain ?? string.Empty;

        if (value.Length > maxCharacters)
        {
            throw new BrowserApiException(StatusCodes.Status413PayloadTooLarge, tooLongCode, $"Значение не должно превышать {maxCharacters} символов.");
        }
        return value;
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new BrowserApiException(StatusCodes.Status404NotFound, "AI_REMOTE_BROWSER_DISABLED", "AI remote-browser отключён.");
        }
    }
}

internal sealed record AiRemoteStartedSession(
    AiRemoteBrowserSession Session,
    string Secret,
    SiteSnapshotResponse Snapshot);

internal sealed record AiRemoteSnapshotOutcome(
    AiRemoteBrowserSession Session,
    SiteSnapshotResponse Snapshot);

internal sealed record AiRemoteScreenshotOutcome(
    AiRemoteBrowserSession Session,
    BrowserScreenshot Screenshot);

internal sealed record AiRemoteActionOutcome(
    AiRemoteBrowserSession Session,
    string Action,
    SiteSnapshotResponse? Snapshot,
    string? Message);
