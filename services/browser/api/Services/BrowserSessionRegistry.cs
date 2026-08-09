using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed partial class BrowserSessionRegistry(
    BrowserPageFactory pageFactory,
    SnapshotBuilder snapshotBuilder,
    BrowserScreenshotService screenshots,
    BrowserCapacityGate capacityGate,
    BrowserOptions options,
    ILogger<BrowserSessionRegistry> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, BrowserSession> _sessions = new();
    private readonly BrowserPageFactory _pageFactory = pageFactory;
    private readonly SnapshotBuilder _snapshotBuilder = snapshotBuilder;
    private readonly BrowserScreenshotService _screenshots = screenshots;
    private readonly BrowserCapacityGate _capacityGate = capacityGate;
    private readonly BrowserOptions _options = options;
    private readonly ILogger<BrowserSessionRegistry> _logger = logger;
    private readonly SemaphoreSlim _createMutex = new(1, 1);
    private int _disposed;

    public int ActiveCount => _sessions.Count;

    public Task<(BrowserSession Session, string RawToken, SiteSnapshotResponse Snapshot)> CreateAsync(
        CreateBrowserSessionRequest request,
        BrowserCaller caller,
        CancellationToken cancellationToken)
        => CreateCoreAsync(request, caller, allowAnonymousInteractive: false, cancellationToken);

    // Internal compatibility entry point for restricted AI remote-browser sessions.
    // It is intentionally not exposed as a normal Browser API endpoint: the public
    // POST /api/browser/sessions contract still requires a real TaskForge bearer
    // token for readOnly=false. The AI bridge protects this session with its own
    // high-entropy capability key and never exposes the raw browser session token.
    internal Task<(BrowserSession Session, string RawToken, SiteSnapshotResponse Snapshot)> CreateAgentInteractiveAsync(
        CreateBrowserSessionRequest request,
        BrowserCaller caller,
        CancellationToken cancellationToken)
        => CreateCoreAsync(request with { ReadOnly = false }, caller, allowAnonymousInteractive: true, cancellationToken);

    private async Task<(BrowserSession Session, string RawToken, SiteSnapshotResponse Snapshot)> CreateCoreAsync(
        CreateBrowserSessionRequest request,
        BrowserCaller caller,
        bool allowAnonymousInteractive,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var width = request.Width ?? 1440;
        var height = request.Height ?? 900;
        var readOnly = request.ReadOnly ?? true;
        if (!readOnly && !caller.IsAuthenticated && !allowAnonymousInteractive)
        {
            throw new BrowserApiException(
                StatusCodes.Status401Unauthorized,
                "AUTHENTICATION_REQUIRED_FOR_MUTATING_SESSION",
                "Анонимные браузерные сессии всегда работают только для чтения. Для readOnly=false используйте обычный TaskForge access token.");
        }

        var wait = System.Math.Clamp(request.WaitMs ?? _options.DefaultWaitMilliseconds, 0, _options.MaxWaitMilliseconds);
        var rawToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;

        BrowserSession? session = null;
        await _createMutex.WaitAsync(cancellationToken);
        try
        {
            EnsureCapacity(caller);
            await using var lease = await _capacityGate.EnterAsync(cancellationToken);
            var handle = await _pageFactory.CreateAsync(request.Site, width, height, readOnly, caller, cancellationToken);
            session = new BrowserSession
            {
                Id = Guid.NewGuid(),
                TokenHash = Hash(rawToken),
                OwnerKey = caller.OwnerKey,
                OwnerIsAuthenticated = caller.IsAuthenticated,
                Handle = handle,
                CreatedAtUtc = now,
                LastAccessAtUtc = now,
                AbsoluteExpiresAtUtc = now.AddMinutes(_options.SessionAbsoluteMinutes),
                IdleTimeout = TimeSpan.FromMinutes(_options.SessionIdleMinutes)
            };

            if (!_sessions.TryAdd(session.Id, session))
            {
                await handle.DisposeAsync();
                throw new BrowserApiException(StatusCodes.Status500InternalServerError, "SESSION_CREATE_FAILED", "Не удалось зарегистрировать браузерную сессию.");
            }
        }
        finally
        {
            _createMutex.Release();
        }

        try
        {
            await using var lease = await _capacityGate.EnterAsync(cancellationToken);
            await _pageFactory.NavigateAsync(session.Handle, request.Path, wait, cancellationToken);
            var snapshot = await _snapshotBuilder.BuildAsync(session.Handle, includeText: true, cancellationToken);
            session.LastAccessAtUtc = DateTimeOffset.UtcNow;
            return (session, rawToken, snapshot);
        }
        catch
        {
            await RemoveAndDisposeAsync(session.Id);
            throw;
        }
    }

    public Task<SiteSnapshotResponse> SnapshotAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        bool includeText,
        int waitMilliseconds,
        CancellationToken cancellationToken)
        => UseAsync(
            id,
            token,
            caller,
            "snapshot",
            async session =>
            {
                if (waitMilliseconds > 0)
                {
                    await _pageFactory.StabilizeAsync(
                        session.Handle,
                        System.Math.Clamp(waitMilliseconds, 0, _options.MaxWaitMilliseconds),
                        cancellationToken);
                }
                return await _snapshotBuilder.BuildAsync(session.Handle, includeText, cancellationToken);
            },
            cancellationToken);

    public Task<BrowserScreenshot> ScreenshotAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        bool fullPage,
        bool annotated,
        CancellationToken cancellationToken)
        => UseAsync(id, token, caller, "screenshot", async session =>
        {
            return await _screenshots.CaptureAsync(session.Handle, fullPage, annotated, cancellationToken);
        }, cancellationToken);

    public Task<BrowserActionResponse> NavigateAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        NavigateBrowserSessionRequest request,
        bool includeSnapshot,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "navigate", async session =>
        {
            await _pageFactory.NavigateAsync(
                session.Handle,
                request.Path,
                System.Math.Clamp(request.WaitMs ?? _options.DefaultWaitMilliseconds, 0, _options.MaxWaitMilliseconds),
                cancellationToken);
        }, includeSnapshot, null, cancellationToken);

    public Task<BrowserActionResponse> ClickAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        ClickBrowserSessionRequest request,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "click", async session =>
        {
            var locator = await ResolveElementAsync(session, request.ElementId);
            await locator.ClickAsync(new LocatorClickOptions
            {
                ClickCount = System.Math.Clamp(request.ClickCount ?? 1, 1, 2)
            }).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, _options.DefaultWaitMilliseconds, cancellationToken);
        }, request.IncludeSnapshot ?? true, request.WaitMs, cancellationToken);

    public Task<BrowserActionResponse> FillAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        FillBrowserSessionRequest request,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "fill", async session =>
        {
            var value = request.Value ?? string.Empty;
            if (value.Length > 20000)
            {
                throw new BrowserApiException(StatusCodes.Status400BadRequest, "VALUE_TOO_LONG", "Значение поля не должно превышать 20000 символов.");
            }

            var locator = await ResolveElementAsync(session, request.ElementId);
            await locator.FillAsync(value).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 100, cancellationToken);
        }, request.IncludeSnapshot ?? true, request.WaitMs, cancellationToken);

    // Internal low-level primitives for GET-only AI compatibility. They operate on
    // the same real Playwright page and are intentionally not separate business APIs.
    internal Task<BrowserActionResponse> InsertTextAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        string elementId,
        string value,
        int? waitMs,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "insert", async session =>
        {
            if (value.Length > 4000)
            {
                throw new BrowserApiException(StatusCodes.Status400BadRequest, "VALUE_TOO_LONG", "Один insert-фрагмент не должен превышать 4000 символов.");
            }
            var locator = await ResolveElementAsync(session, elementId);
            await locator.FocusAsync().WaitAsync(cancellationToken);
            await session.Handle.Page.Keyboard.InsertTextAsync(value).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 100, cancellationToken);
        }, true, waitMs, cancellationToken);

    internal Task<BrowserActionResponse> KeyboardPressAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        string key,
        int? waitMs,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "key", async session =>
        {
            var normalized = (key ?? string.Empty).Trim();
            if (normalized.Length is < 1 or > 64 || normalized.Any(char.IsControl))
            {
                throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_KEY", "Укажите корректную клавишу Playwright.");
            }
            await session.Handle.Page.Keyboard.PressAsync(normalized).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 200, cancellationToken);
        }, true, waitMs, cancellationToken);

    internal Task<BrowserActionResponse> MouseClickAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        double x,
        double y,
        int clickCount,
        int? waitMs,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "mouse-click", async session =>
        {
            if (x < 0 || x > session.Handle.Width || y < 0 || y > session.Handle.Height)
            {
                throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_MOUSE_COORDINATES", "Координаты должны находиться внутри текущего viewport.");
            }
            await session.Handle.Page.Mouse.ClickAsync((float)x, (float)y, new MouseClickOptions
            {
                ClickCount = System.Math.Clamp(clickCount, 1, 2)
            }).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 250, cancellationToken);
        }, true, waitMs, cancellationToken);

    public Task<BrowserActionResponse> PressAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        PressBrowserSessionRequest request,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "press", async session =>
        {
            var key = (request.Key ?? string.Empty).Trim();
            if (key.Length is < 1 or > 64 || key.Any(char.IsControl))
            {
                throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_KEY", "Укажите корректную клавишу Playwright, например Enter, Escape или Control+A.");
            }

            var locator = await ResolveElementAsync(session, request.ElementId);
            await locator.PressAsync(key).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 250, cancellationToken);
        }, request.IncludeSnapshot ?? true, request.WaitMs, cancellationToken);

    public Task<BrowserActionResponse> SelectAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        SelectBrowserSessionRequest request,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "select", async session =>
        {
            var value = request.Value ?? string.Empty;
            if (value.Length > 1000)
            {
                throw new BrowserApiException(StatusCodes.Status400BadRequest, "VALUE_TOO_LONG", "Значение select слишком длинное.");
            }

            var locator = await ResolveElementAsync(session, request.ElementId);
            await locator.SelectOptionAsync(value).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 150, cancellationToken);
        }, request.IncludeSnapshot ?? true, request.WaitMs, cancellationToken);

    public Task<BrowserActionResponse> HoverAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        HoverBrowserSessionRequest request,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "hover", async session =>
        {
            var locator = await ResolveElementAsync(session, request.ElementId);
            await locator.HoverAsync().WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 150, cancellationToken);
        }, request.IncludeSnapshot ?? true, request.WaitMs, cancellationToken);

    public Task<BrowserActionResponse> CheckAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        CheckBrowserSessionRequest request,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "check", async session =>
        {
            var locator = await ResolveElementAsync(session, request.ElementId);
            await locator.SetCheckedAsync(request.Checked ?? true).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, 150, cancellationToken);
        }, request.IncludeSnapshot ?? true, request.WaitMs, cancellationToken);

    public Task<BrowserActionResponse> ScrollAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        ScrollBrowserSessionRequest request,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "scroll", async session =>
        {
            if (!string.IsNullOrWhiteSpace(request.ElementId))
            {
                var locator = await ResolveElementAsync(session, request.ElementId);
                await locator.ScrollIntoViewIfNeededAsync().WaitAsync(cancellationToken);
            }
            else
            {
                var x = (float)System.Math.Clamp(request.DeltaX ?? 0d, -5000d, 5000d);
                var y = (float)System.Math.Clamp(request.DeltaY ?? 700d, -5000d, 5000d);
                await session.Handle.Page.Mouse.WheelAsync(x, y).WaitAsync(cancellationToken);
            }

            await _pageFactory.StabilizeAsync(session.Handle, 150, cancellationToken);
        }, request.IncludeSnapshot ?? true, request.WaitMs, cancellationToken);

    public Task<BrowserActionResponse> BackAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        bool includeSnapshot,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "back", async session =>
        {
            await session.Handle.Page.GoBackAsync(new PageGoBackOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = System.Math.Clamp(_options.NavigationTimeoutSeconds, 1, 120) * 1000
            }).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, _options.DefaultWaitMilliseconds, cancellationToken);
        }, includeSnapshot, null, cancellationToken);

    public Task<BrowserActionResponse> ReloadAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        bool includeSnapshot,
        CancellationToken cancellationToken)
        => UseActionAsync(id, token, caller, "reload", async session =>
        {
            await session.Handle.Page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = System.Math.Clamp(_options.NavigationTimeoutSeconds, 1, 120) * 1000
            }).WaitAsync(cancellationToken);
            await _pageFactory.StabilizeAsync(session.Handle, _options.DefaultWaitMilliseconds, cancellationToken);
        }, includeSnapshot, null, cancellationToken);

    public async Task CloseAsync(Guid id, string? token, BrowserCaller caller)
    {
        var session = Resolve(id, token, caller);
        if (_sessions.TryRemove(id, out _)) await DisposeSessionAsync(session);
    }

    public async Task<int> CleanupExpiredAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _sessions.Values.Where(x => x.IsExpired(now)).Select(x => x.Id).ToArray();
        foreach (var id in expired) await RemoveAndDisposeAsync(id);
        return expired.Length;
    }

    private async Task<BrowserActionResponse> UseActionAsync(
        Guid id,
        string? token,
        BrowserCaller caller,
        string action,
        Func<BrowserSession, Task> operation,
        bool includeSnapshot,
        int? waitMilliseconds,
        CancellationToken cancellationToken)
        => await UseAsync(id, token, caller, action, async session =>
        {
            await operation(session);
            await _pageFactory.EnsureSafeStateAsync(session.Handle, cancellationToken);
            if (waitMilliseconds is > 0)
            {
                await _pageFactory.StabilizeAsync(
                    session.Handle,
                    System.Math.Clamp(waitMilliseconds.Value, 0, _options.MaxWaitMilliseconds),
                    cancellationToken);
            }
            var page = session.Handle.Page;
            var snapshot = includeSnapshot
                ? await _snapshotBuilder.BuildAsync(session.Handle, true, cancellationToken)
                : null;
            return new BrowserActionResponse(
                session.Id,
                action,
                SafePageUrl(page.Url),
                await page.TitleAsync().WaitAsync(cancellationToken),
                DateTimeOffset.UtcNow,
                snapshot);
        }, cancellationToken);

    private async Task<T> UseAsync<T>(
        Guid id,
        string? token,
        BrowserCaller caller,
        string operation,
        Func<BrowserSession, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var session = Resolve(id, token, caller);
        var expired = false;
        await session.Mutex.WaitAsync(cancellationToken);
        try
        {
            if (session.IsClosed)
            {
                throw new BrowserApiException(StatusCodes.Status410Gone, "SESSION_CLOSED", "Браузерная сессия уже закрыта.");
            }

            if (session.IsExpired(DateTimeOffset.UtcNow))
            {
                expired = true;
                throw new BrowserApiException(StatusCodes.Status410Gone, "SESSION_EXPIRED", "Браузерная сессия истекла.");
            }

            await using var lease = await _capacityGate.EnterAsync(cancellationToken);
            session.LastAccessAtUtc = DateTimeOffset.UtcNow;
            var result = await action(session);
            session.LastAccessAtUtc = DateTimeOffset.UtcNow;
            return result;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogInformation(ex, "Browser session {SessionId} operation {Operation} failed.", id, operation);
            throw new BrowserApiException(
                StatusCodes.Status409Conflict,
                "BROWSER_ACTION_FAILED",
                $"Chromium не смог выполнить действие '{operation}': {SafeMessage(ex.Message)}");
        }
        finally
        {
            session.Mutex.Release();
            if (expired) await RemoveAndDisposeAsync(id);
        }
    }

    private BrowserSession Resolve(Guid id, string? rawToken, BrowserCaller caller)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new BrowserApiException(StatusCodes.Status404NotFound, "SESSION_NOT_FOUND", "Браузерная сессия не найдена.");
        }

        if (string.IsNullOrWhiteSpace(rawToken) || !FixedEquals(session.TokenHash, Hash(rawToken)))
        {
            throw new BrowserApiException(StatusCodes.Status401Unauthorized, "INVALID_SESSION_TOKEN", "Не указан или недействителен токен браузерной сессии.");
        }

        if (!BrowserSessionAccessPolicy.CanUse(
                session.OwnerIsAuthenticated,
                session.OwnerKey,
                caller.IsAuthenticated,
                caller.OwnerKey))
        {
            throw new BrowserApiException(
                StatusCodes.Status403Forbidden,
                "SESSION_OWNER_MISMATCH",
                "Авторизованная браузерная сессия принадлежит другому TaskForge-пользователю.");
        }

        return session;
    }

    private static async Task<ILocator> ResolveElementAsync(BrowserSession session, string? elementId)
    {
        var raw = (elementId ?? string.Empty).Trim();
        ILocator locator;
        if (TransientElementIdPattern().IsMatch(raw))
        {
            var id = raw.ToLowerInvariant();
            locator = session.Handle.Page.Locator($"[data-taskforge-agent-id=\"{id}\"]");
        }
        else if (AutomationElementIdPattern().IsMatch(raw))
        {
            locator = session.Handle.Page.Locator($"[data-taskforge-automation-id=\"{raw}\"]");
        }
        else
        {
            throw new BrowserApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_ELEMENT_ID",
                "Используйте tfN из последнего snapshot или стабильный automationId, например register-first-name или solution-code-editor.");
        }

        var count = await locator.CountAsync();
        if (count == 0)
        {
            throw new BrowserApiException(
                StatusCodes.Status409Conflict,
                "STALE_ELEMENT_ID",
                "Элемент больше не существует или текущая страница изменилась. Получите новый snapshot.");
        }
        if (count > 1)
        {
            throw new BrowserApiException(
                StatusCodes.Status409Conflict,
                "AMBIGUOUS_AUTOMATION_ID",
                "automationId должен однозначно определять один элемент страницы.");
        }

        return locator;
    }

    private void EnsureCapacity(BrowserCaller caller)
    {
        if (_sessions.Count >= _options.MaxActiveSessions)
        {
            throw new BrowserApiException(StatusCodes.Status503ServiceUnavailable, "SESSION_CAPACITY_EXHAUSTED", "Достигнут общий предел активных браузерных сессий.", 5);
        }

        var ownerLimit = caller.IsAuthenticated
            ? _options.MaxAuthenticatedSessionsPerOwner
            : _options.MaxAnonymousSessionsPerOwner;
        if (_sessions.Values.Count(x => string.Equals(x.OwnerKey, caller.OwnerKey, StringComparison.Ordinal)) >= ownerLimit)
        {
            throw new BrowserApiException(StatusCodes.Status429TooManyRequests, "SESSION_OWNER_LIMIT", "Для этого пользователя или адреса уже открыто максимальное число браузерных сессий.", 10);
        }
    }

    private async Task RemoveAndDisposeAsync(Guid id)
    {
        if (_sessions.TryRemove(id, out var session)) await DisposeSessionAsync(session);
    }

    private static async Task DisposeSessionAsync(BrowserSession session)
    {
        if (!session.TryBeginClose()) return;
        await session.Mutex.WaitAsync();
        try
        {
            await session.Handle.DisposeAsync();
        }
        finally
        {
            session.Mutex.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var ids = _sessions.Keys.ToArray();
        foreach (var id in ids) await RemoveAndDisposeAsync(id);
        _createMutex.Dispose();
    }

    private static string SafePageUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path) + (string.IsNullOrWhiteSpace(uri.Query) ? string.Empty : "?[query-redacted]")
            : value;

    private static string SafeMessage(string value)
        => value.Length <= 500 ? value : value[..500] + "…";

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string a, string b)
    {
        var aa = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
    }

    [GeneratedRegex("^tf[1-9][0-9]{0,5}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TransientElementIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$", RegexOptions.CultureInvariant)]
    private static partial Regex AutomationElementIdPattern();
}

public sealed class BrowserSession
{
    private int _closed;

    public Guid Id { get; init; }
    public required string TokenHash { get; init; }
    public required string OwnerKey { get; init; }
    public required bool OwnerIsAuthenticated { get; init; }
    public required BrowserPageHandle Handle { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset AbsoluteExpiresAtUtc { get; init; }
    public required TimeSpan IdleTimeout { get; init; }
    public DateTimeOffset LastAccessAtUtc { get; set; }
    public SemaphoreSlim Mutex { get; } = new(1, 1);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public bool TryBeginClose() => Interlocked.Exchange(ref _closed, 1) == 0;

    public bool IsExpired(DateTimeOffset now)
        => now >= AbsoluteExpiresAtUtc || now - LastAccessAtUtc >= IdleTimeout;
}
