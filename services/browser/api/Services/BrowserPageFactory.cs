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
            await ConfigureContextAsync(context, baseUri, caller);
            await context.RouteAsync("**/*", async route => await RouteRequestAsync(route, baseUri, readOnly));

            var page = await context.NewPageAsync();
            var events = new BrowserEventBuffer(_options.MaxEventEntries);
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
        await handle.Page.GotoAsync(target.AbsoluteUri, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = System.Math.Clamp(_options.NavigationTimeoutSeconds, 1, 120) * 1000
        }).WaitAsync(cancellationToken);

        await StabilizeAsync(handle, waitMilliseconds, cancellationToken);
    }

    public async Task StabilizeAsync(
        BrowserPageHandle handle,
        int waitMilliseconds,
        CancellationToken cancellationToken)
    {
        await StabilizePageAsync(handle.Page, waitMilliseconds, cancellationToken);
        await EnsureSafeStateAsync(handle, cancellationToken);
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
            await StabilizePageAsync(handle.Page, 200, cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogInformation(ex, "Failed to restore the last safe TaskForge page after blocked navigation.");
        }

        throw new BrowserApiException(
            StatusCodes.Status409Conflict,
            "NAVIGATION_OUTSIDE_TASKFORGE_BLOCKED",
            "Переход за пределы выбранного сайта TaskForge заблокирован. Сессия возвращена на последнюю безопасную страницу.");
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
        page.RequestFailed += (_, request) => events.AddFailure(request.Method, request.Url, request.ResourceType, request.Failure);
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

    private async Task RouteRequestAsync(IRoute route, Uri siteBaseUri, bool readOnly)
    {
        try
        {
            var request = route.Request;
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || !_urlPolicy.IsAllowedRequest(uri))
            {
                _logger.LogDebug("Blocked browser request to non-allowlisted origin: {Url}", SafeRequestUrl(request.Url));
                await route.AbortAsync("blockedbyclient");
                return;
            }

            if (request.ResourceType.Equals("document", StringComparison.OrdinalIgnoreCase)
                && !BrowserUrlPolicy.SameOrigin(siteBaseUri, uri))
            {
                _logger.LogDebug("Blocked document navigation outside the selected TaskForge site: {Url}", SafeRequestUrl(request.Url));
                await route.AbortAsync("blockedbyclient");
                return;
            }

            // Never allow a Chromium page to call the renderer/session API itself.
            // Without this boundary a same-origin page could recursively create more
            // Chromium work through GET /api/site/render or /api/browser/sessions.
            if (_urlPolicy.IsBrowserApiEndpoint(uri))
            {
                _logger.LogDebug("Blocked recursive Browser API request from Chromium: {Method} {Path}", request.Method, uri.AbsolutePath);
                await route.AbortAsync("blockedbyclient");
                return;
            }

            if (readOnly && uri.Scheme is "ws" or "wss")
            {
                _logger.LogDebug("Blocked WebSocket in read-only browser session: {Url}", SafeRequestUrl(request.Url));
                await route.AbortAsync("blockedbyclient");
                return;
            }

            if (readOnly
                && BrowserUrlPolicy.SameOrigin(siteBaseUri, uri)
                && !IsSafeMethod(request.Method))
            {
                _logger.LogDebug("Blocked mutating same-origin request in read-only browser session: {Method} {Path}", request.Method, uri.AbsolutePath);
                await route.AbortAsync("blockedbyclient");
                return;
            }

            if (!BrowserUrlPolicy.SameOrigin(siteBaseUri, uri)
                && (uri.Scheme is "ws" or "wss" || !IsSafeMethod(request.Method)))
            {
                await route.AbortAsync("blockedbyclient");
                return;
            }

            await route.ContinueAsync();
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Browser route was already handled or closed.");
        }
    }

    private async Task StabilizePageAsync(IPage page, int waitMilliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wait = System.Math.Clamp(waitMilliseconds, 0, _options.MaxWaitMilliseconds);

        try
        {
            await page.WaitForFunctionAsync(
                "() => document.readyState === 'complete' || document.readyState === 'interactive'",
                null,
                new PageWaitForFunctionOptions { Timeout = 2000 }).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Goto already waited for DOMContentLoaded. Continue with a best-effort snapshot.
        }

        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = System.Math.Min(2500, System.Math.Max(500, _options.ActionTimeoutSeconds * 1000))
            }).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Long-polling and analytics may keep the network active indefinitely.
        }

        try
        {
            await page.EvaluateAsync("() => document.fonts?.ready || Promise.resolve()").WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Font loading is not required for semantic inspection.
        }

        if (wait > 0) await page.WaitForTimeoutAsync(wait).WaitAsync(cancellationToken);
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
