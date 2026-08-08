using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed class BrowserPageFactory(
    BrowserRuntime runtime,
    BrowserUrlPolicy urlPolicy,
    BrowserOptions options,
    ILogger<BrowserPageFactory> logger)
{
    private readonly BrowserRuntime _runtime = runtime;
    private readonly BrowserUrlPolicy _urlPolicy = urlPolicy;
    private readonly BrowserOptions _options = options;
    private readonly ILogger<BrowserPageFactory> _logger = logger;

    public async Task<BrowserPageHandle> CreateAsync(
        string? site,
        int width,
        int height,
        bool readOnly,
        BrowserCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateViewport(width, height);
        var (siteKey, baseUri) = _urlPolicy.ResolveSite(site);
        var context = await _runtime.NewContextAsync(width, height, cancellationToken);
        context.SetDefaultTimeout(System.Math.Clamp(_options.ActionTimeoutSeconds, 1, 60) * 1000);
        context.SetDefaultNavigationTimeout(System.Math.Clamp(_options.NavigationTimeoutSeconds, 1, 120) * 1000);

        try
        {
            var events = new BrowserEventBuffer(_options.MaxEventEntries);
            await ConfigureContextAsync(context, baseUri, caller);
            await context.RouteAsync("**/*", async route => await RouteRequestAsync(route, baseUri, readOnly, events));

            var page = await context.NewPageAsync();
            AttachEvents(page, events);

            return new BrowserPageHandle
            {
                Site = siteKey,
                SiteBaseUri = baseUri,
                Caller = caller,
                ReadOnly = readOnly,
                Context = context,
                Page = page,
                Events = events,
                Width = width,
                Height = height
            };
        }
        catch
        {
            await context.CloseAsync();
            throw;
        }
    }

    public async Task NavigateAsync(
        BrowserPageHandle handle,
        string? path,
        int waitMilliseconds,
        CancellationToken cancellationToken)
    {
        var target = _urlPolicy.BuildPageUri(handle.SiteBaseUri, path);
        var started = Stopwatch.StartNew();
        handle.Readiness = new SnapshotReadiness { Stage = "navigation" };

        try
        {
            await handle.Page.GotoAsync(target.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = System.Math.Clamp(_options.NavigationTimeoutSeconds, 1, 120) * 1000
            }).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            var details = await BuildFailureDetailsAsync(handle, "navigation", started.ElapsedMilliseconds, cancellationToken);
            throw new BrowserApiException(
                StatusCodes.Status504GatewayTimeout,
                "PAGE_NAVIGATION_TIMEOUT",
                "Chromium не дождался загрузки страницы TaskForge до DOMContentLoaded.",
                details: details,
                innerException: ex);
        }

        await StabilizeAsync(handle, waitMilliseconds, cancellationToken);
    }

    public async Task StabilizeAsync(
        BrowserPageHandle handle,
        int waitMilliseconds,
        CancellationToken cancellationToken)
    {
        handle.Readiness = await StabilizePageAsync(handle, waitMilliseconds, cancellationToken);
        await EnsureSafeStateAsync(handle, cancellationToken);
        handle.Readiness.PendingRequestCount = handle.Events.PendingRequestCount;
        handle.Readiness.PendingRequests = handle.Events.PendingRequests();
    }

    public async Task EnsureSafeStateAsync(BrowserPageHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var extraPage in handle.Context.Pages.Where(page => !ReferenceEquals(page, handle.Page)).ToArray())
        {
            try
            {
                await extraPage.CloseAsync().WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                _logger.LogDebug(ex, "Failed to close an unexpected browser popup.");
            }
        }

        if (handle.Page.IsClosed)
        {
            throw new BrowserApiException(StatusCodes.Status409Conflict, "PAGE_CLOSED", "Основная вкладка браузерной сессии была закрыта.");
        }

        if (Uri.TryCreate(handle.Page.Url, UriKind.Absolute, out var current)
            && BrowserUrlPolicy.SameOrigin(handle.SiteBaseUri, current))
        {
            handle.LastSafeUrl = current.AbsoluteUri;
            return;
        }

        var unsafeUrl = SafeRequestUrl(handle.Page.Url);
        var fallback = handle.LastSafeUrl ?? handle.SiteBaseUri.AbsoluteUri;
        _logger.LogWarning("Browser page attempted to leave selected TaskForge origin. unsafe={UnsafeUrl} fallback={Fallback}", unsafeUrl, SafeRequestUrl(fallback));

        try
        {
            await handle.Page.GotoAsync(fallback, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = System.Math.Clamp(_options.NavigationTimeoutSeconds, 1, 120) * 1000
            }).WaitAsync(cancellationToken);
            handle.Readiness = await StabilizePageAsync(handle, 200, cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogInformation(ex, "Failed to restore the last safe TaskForge page after blocked navigation.");
        }

        throw new BrowserApiException(
            StatusCodes.Status409Conflict,
            "NAVIGATION_OUTSIDE_TASKFORGE_BLOCKED",
            "Переход за пределы выбранного сайта TaskForge заблокирован. Сессия возвращена на последнюю безопасную страницу.",
            details: new { unsafeUrl, fallback = SafeRequestUrl(fallback) });
    }

    public void ValidateViewport(int width, int height)
    {
        if (width < _options.MinViewportWidth || width > _options.MaxViewportWidth
            || height < _options.MinViewportHeight || height > _options.MaxViewportHeight)
        {
            throw new BrowserApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_VIEWPORT",
                $"Viewport должен быть в диапазоне {_options.MinViewportWidth}–{_options.MaxViewportWidth} × {_options.MinViewportHeight}–{_options.MaxViewportHeight} CSS px.");
        }
    }

    private async Task ConfigureContextAsync(IBrowserContext context, Uri site, BrowserCaller caller)
    {
        await context.AddInitScriptAsync(PerformanceObserverScript);

        if (!caller.IsAuthenticated || string.IsNullOrWhiteSpace(caller.AccessToken)) return;

        await context.AddCookiesAsync(new[]
        {
            new Cookie
            {
                Name = "tf_at",
                Value = caller.AccessToken,
                Domain = site.Host,
                Path = "/",
                HttpOnly = true,
                Secure = site.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                SameSite = SameSiteAttribute.Lax
            }
        });

        var serializedToken = JsonSerializer.Serialize(caller.AccessToken);
        await context.AddInitScriptAsync($"window.__TASKFORGE_BROWSER_ACCESS_TOKEN__ = {serializedToken};");
    }

    private void AttachEvents(IPage page, BrowserEventBuffer events)
    {
        page.Console += (_, message) => events.AddConsole(message.Type, message.Text);
        page.PageError += (_, error) => events.AddConsole("pageerror", error);
        page.Request += (_, request) => events.RequestStarted(request.Method, request.Url, request.ResourceType);
        page.RequestFinished += (_, request) => events.RequestFinished(request.Method, request.Url, request.ResourceType);
        page.RequestFailed += (_, request) => events.RequestFailed(request.Method, request.Url, request.ResourceType, request.Failure);
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                events.AddHttpError(response.Request.Method, response.Url, response.Request.ResourceType, response.Status);
            }
        };
        page.Popup += async (_, popup) =>
        {
            try
            {
                await popup.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // The popup may already have been blocked or closed by the context cleanup.
            }
        };
    }

    private async Task RouteRequestAsync(IRoute route, Uri siteBaseUri, bool readOnly, BrowserEventBuffer events)
    {
        try
        {
            var request = route.Request;
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || !_urlPolicy.IsAllowedRequest(uri))
            {
                await AbortExpectedAsync(route, events, "origin_not_allowlisted");
                return;
            }

            if (request.ResourceType.Equals("document", StringComparison.OrdinalIgnoreCase)
                && !BrowserUrlPolicy.SameOrigin(siteBaseUri, uri))
            {
                await AbortExpectedAsync(route, events, "document_navigation_outside_site");
                return;
            }

            // Never allow a Chromium page to call the renderer/session API itself.
            if (_urlPolicy.IsBrowserApiEndpoint(uri))
            {
                await AbortExpectedAsync(route, events, "browser_api_recursion");
                return;
            }

            if (readOnly && uri.Scheme is "ws" or "wss")
            {
                await AbortExpectedAsync(route, events, "read_only_websocket");
                return;
            }

            if (readOnly
                && BrowserUrlPolicy.SameOrigin(siteBaseUri, uri)
                && !IsSafeMethod(request.Method))
            {
                await AbortExpectedAsync(route, events, "read_only_mutation");
                return;
            }

            if (!BrowserUrlPolicy.SameOrigin(siteBaseUri, uri)
                && (uri.Scheme is "ws" or "wss" || !IsSafeMethod(request.Method)))
            {
                await AbortExpectedAsync(route, events, "external_mutation_or_websocket");
                return;
            }

            await route.ContinueAsync();
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Browser route was already handled or closed.");
        }
    }

    private async Task AbortExpectedAsync(IRoute route, BrowserEventBuffer events, string reason)
    {
        var request = route.Request;
        events.AddPolicyBlocked(request.Method, request.Url, request.ResourceType, reason);
        _logger.LogDebug(
            "Inspector policy blocked Chromium request. reason={Reason} method={Method} url={Url}",
            reason,
            request.Method,
            SafeRequestUrl(request.Url));
        try
        {
            await route.AbortAsync("blockedbyclient");
        }
        catch
        {
            // Do not leave a stale suppression marker if Playwright failed before
            // producing the matching RequestFailed event.
            events.CancelExpectedFailure(request.Method, request.Url, request.ResourceType);
            throw;
        }
    }

    private async Task<SnapshotReadiness> StabilizePageAsync(
        BrowserPageHandle handle,
        int waitMilliseconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var wait = System.Math.Clamp(waitMilliseconds, 0, _options.MaxWaitMilliseconds);
        var timedOut = false;
        var stage = "dom-ready";

        try
        {
            await handle.Page.WaitForFunctionAsync(
                "() => document.readyState === 'complete' || document.readyState === 'interactive'",
                null,
                new PageWaitForFunctionOptions { Timeout = 2000 }).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            timedOut = true;
        }

        stage = "app-ready";
        try
        {
            await handle.Page.WaitForFunctionAsync(
                ReadyPredicateScript,
                null,
                new PageWaitForFunctionOptions
                {
                    Timeout = System.Math.Clamp(_options.AppReadyTimeoutMilliseconds, 250, 15000)
                }).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // The marker is a best-effort signal. A mounted root remains capturable.
            timedOut = true;
        }

        stage = "fonts";
        try
        {
            var fontTimeout = System.Math.Clamp(_options.FontReadyTimeoutMilliseconds, 100, 10000);
            await handle.Page.EvaluateAsync(
                "timeout => Promise.race([document.fonts?.ready || Promise.resolve(), new Promise(resolve => setTimeout(resolve, timeout))])",
                fontTimeout).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Font loading is not required for semantic inspection.
        }

        stage = "settle";
        if (wait > 0) await handle.Page.WaitForTimeoutAsync(wait).WaitAsync(cancellationToken);

        var probe = await ReadinessProbeAsync(handle.Page, cancellationToken);
        var effectiveAppReady = probe.AppReady || (!probe.AppReadyMarkerPresent && probe.RootMounted);
        return new SnapshotReadiness
        {
            Stage = effectiveAppReady ? "ready" : stage,
            PageReadyState = probe.PageReadyState,
            AppReadyMarkerPresent = probe.AppReadyMarkerPresent,
            AppReady = effectiveAppReady,
            RootMounted = probe.RootMounted,
            TimedOut = timedOut && !effectiveAppReady,
            StabilizationMilliseconds = (int)System.Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
            PendingRequestCount = handle.Events.PendingRequestCount,
            PendingRequests = handle.Events.PendingRequests()
        };
    }

    private async Task<object> BuildFailureDetailsAsync(
        BrowserPageHandle handle,
        string stage,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        var probe = await ReadinessProbeAsync(handle.Page, cancellationToken);
        string title;
        try
        {
            title = await handle.Page.TitleAsync().WaitAsync(cancellationToken);
        }
        catch
        {
            title = string.Empty;
        }

        return new
        {
            stage,
            elapsedMilliseconds,
            url = SafeRequestUrl(handle.Page.Url),
            title,
            pageReadyState = probe.PageReadyState,
            appReadyMarkerPresent = probe.AppReadyMarkerPresent,
            appReady = probe.AppReady,
            rootMounted = probe.RootMounted,
            pendingRequestCount = handle.Events.PendingRequestCount,
            pendingRequests = handle.Events.PendingRequests(),
            recentConsole = handle.Events.Console().TakeLast(10),
            recentNetworkFailures = handle.Events.NetworkFailures().TakeLast(10),
            policyBlockedRequests = handle.Events.PolicyBlockedRequests().TakeLast(10)
        };
    }

    private static async Task<ReadyProbe> ReadinessProbeAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await page.EvaluateAsync<ReadyProbe>(ReadyProbeScript).WaitAsync(cancellationToken) ?? new ReadyProbe();
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return new ReadyProbe();
        }
    }

    private static bool IsSafeMethod(string method)
        => method.Equals("GET", StringComparison.OrdinalIgnoreCase)
           || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
           || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static string SafeRequestUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "invalid-or-non-http-url";
        return uri.GetLeftPart(UriPartial.Path);
    }

    private sealed class ReadyProbe
    {
        public string PageReadyState { get; set; } = string.Empty;
        public bool AppReadyMarkerPresent { get; set; }
        public bool AppReady { get; set; }
        public bool RootMounted { get; set; }
    }

    private const string ReadyPredicateScript = """
() => {
  const html = document.documentElement;
  const markerPresent = html.hasAttribute('data-taskforge-ready');
  const appReady = html.dataset.taskforgeReady === 'true';
  const root = document.querySelector('#root, [data-reactroot], main');
  const rootMounted = Boolean(root && (root.childElementCount > 0 || String(root.textContent || '').trim().length > 0));
  return appReady || (!markerPresent && rootMounted);
}
""";

    private const string ReadyProbeScript = """
() => {
  const html = document.documentElement;
  const root = document.querySelector('#root, [data-reactroot], main');
  return {
    pageReadyState: document.readyState || '',
    appReadyMarkerPresent: html.hasAttribute('data-taskforge-ready'),
    appReady: html.dataset.taskforgeReady === 'true',
    rootMounted: Boolean(root && (root.childElementCount > 0 || String(root.textContent || '').trim().length > 0))
  };
}
""";

    private const string PerformanceObserverScript = """
(() => {
  const metrics = window.__TASKFORGE_BROWSER_METRICS__ = {
    largestContentfulPaint: 0,
    cumulativeLayoutShift: 0,
    longTaskCount: 0,
    longTaskDuration: 0
  };

  try {
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        metrics.largestContentfulPaint = Math.max(metrics.largestContentfulPaint, Number(entry.startTime || 0));
      }
    }).observe({ type: 'largest-contentful-paint', buffered: true });
  } catch {}

  try {
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        if (!entry.hadRecentInput) metrics.cumulativeLayoutShift += Number(entry.value || 0);
      }
    }).observe({ type: 'layout-shift', buffered: true });
  } catch {}

  try {
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        metrics.longTaskCount += 1;
        metrics.longTaskDuration += Number(entry.duration || 0);
      }
    }).observe({ type: 'longtask', buffered: true });
  } catch {}
})();
""";
}
