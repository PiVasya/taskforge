using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Infrastructure;
using TaskForge.Browser.Api.Security;
using TaskForge.Browser.Api.Services;

namespace TaskForge.Browser.Api.Endpoints;

public static class AiRemoteBrowserEndpoints
{
    private const string ApiVersion = "1.0";
    private static readonly JsonSerializerOptions HtmlJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static WebApplication MapAiRemoteBrowserEndpoints(this WebApplication app)
    {
        MapDiscovery(app);
        MapStart(app);
        MapRead(app);
        MapActions(app);
        return app;
    }

    private static void MapDiscovery(WebApplication app)
    {
        app.MapGet("/.well-known/taskforge-ai-browser.json", (HttpRequest request, AiRemoteBrowserOptions options, BrowserOptions browserOptions, BrowserRateLimitOptions rateOptions) =>
        {
            var root = PublicRoot(request);
            return Results.Json(new
            {
                name = "TaskForge AI Remote Browser",
                version = ApiVersion,
                enabled = options.Enabled,
                purpose = "Low-level remote control of the real TaskForge Chromium UI. The agent, not TaskForge, decides what to open, type, click and solve.",
                start = $"{root}/api/ai/browser/start",
                workbench = $"{root}/ai-browser",
                preferredFullHttpApi = $"{root}/api/browser/openapi.json",
                primitives = new[] { "snapshot", "screenshot", "view", "navigate", "click", "mouse-click", "fill", "insert", "select", "press", "key", "hover", "check", "scroll", "wait", "back", "reload", "close" },
                compatibility = new
                {
                    getOnlyActions = options.AllowGetMutations,
                    stableAutomationIds = true,
                    transientElementIds = true,
                    authenticatedScreenshots = true,
                    fullPageScreenshots = true,
                    accountCreationIsNotAutomated = true,
                    agentControlsRealRegistrationAndLoginForms = true,
                    sessionLifetime = new
                    {
                        configuredIdleMinutes = options.SessionIdleMinutes,
                        configuredAbsoluteMinutes = options.SessionAbsoluteMinutes,
                        effectiveIdleMinutes = options.GetEffectiveSessionIdleMinutes(browserOptions),
                        effectiveAbsoluteMinutes = options.GetEffectiveSessionAbsoluteMinutes(browserOptions)
                    }
                },
                rateLimits = new
                {
                    start = new { limit = options.StartLimit, windowSeconds = options.StartWindowSeconds },
                    confirm = new { limit = options.ConfirmLimit, windowSeconds = options.ConfirmWindowSeconds },
                    action = new { limit = options.ActionLimit, windowSeconds = options.ActionWindowSeconds },
                    screenshot = new { limit = options.ScreenshotLimit, windowSeconds = options.ScreenshotWindowSeconds },
                    authenticatedAiMultiplier = Math.Clamp(rateOptions.AuthenticatedAiMultiplier, 1, 20),
                    note = "Authenticated accountType=ai outer Browser calls may receive the AI multiplier; anonymous capability traffic remains bounded by the base remote limits and gateway protection.",
                    headers = new[] { "RateLimit-Limit", "RateLimit-Remaining", "RateLimit-Reset", "RateLimit-Policy", "X-RateLimit-Reset", "X-TaskForge-AI-Rate-Multiplier", "Retry-After" },
                    resetSemantics = "RateLimit-Reset is seconds until reset; X-RateLimit-Reset is the UTC Unix timestamp."
                }
            });
        }).WithName("GetTaskForgeAiRemoteBrowserDiscovery").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/ai-browser", (HttpRequest request) =>
        {
            var root = PublicRoot(request);
            var start = $"{root}/api/ai/browser/start?format=html";
            var html = $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex,nofollow,noarchive"><meta name="referrer" content="no-referrer">
<title>TaskForge AI Remote Browser</title>
<style>body{font:16px/1.5 system-ui;max-width:980px;margin:40px auto;padding:0 20px}code,pre{background:#111;color:#eee;border-radius:6px}code{padding:.15rem .35rem}pre{padding:1rem;overflow:auto}a{font-weight:700}</style></head><body>
<h1>TaskForge AI Remote Browser</h1>
<p>This is a low-level compatibility controller over the existing Browser API. It does not choose courses, assignments or answers. The agent sees the real page and decides every action.</p>
<ol><li><a href="{{WebUtility.HtmlEncode(start)}}">Create a side-effect-free start challenge</a>.</li>
<li>Open the returned confirmation URL to create a temporary writable Chromium session.</li>
<li>Use snapshot or visual view, then navigate/click/fill/select/check/press as needed.</li>
<li>For an AI-marked account, the agent may navigate to <code>/register?accountType=ai</code> and fill the ordinary registration UI itself.</li></ol>
<p>After login, the same Chromium context keeps the normal TaskForge auth cookies, so private pages and screenshots remain available through the same remote-browser session.</p>
<p><a href="{{root}}/.well-known/taskforge-ai-browser.json">Discovery JSON</a> · <a href="{{root}}/api/browser/openapi.json">Normal Browser API OpenAPI</a> · <a href="{{root}}/ai-access">Crawler access</a></p>
</body></html>
""";
            return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
        }).WithName("GetTaskForgeAiRemoteBrowserWorkbench").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser", (HttpRequest request) => Results.Redirect($"{PublicRoot(request)}/.well-known/taskforge-ai-browser.json", permanent: false))
            .ExcludeFromDescription().AllowAnonymous();
    }

    private static void MapStart(WebApplication app)
    {
        app.MapGet("/api/ai/browser/start", async (
            HttpContext http,
            string? site,
            string? path,
            int? width,
            int? height,
            int? waitMs,
            string? format,
            AiRemoteBrowserService remote,
            AiRemoteBrowserOptions options,
            BrowserCallerResolver callers,
            RedisFixedWindowRateLimiter limiter,
            CancellationToken ct) =>
        {
            var caller = callers.Resolve(http);
            callers.ThrowIfInvalidCredential(caller);
            await EnforceRateLimit(http, limiter, caller, "ai-remote-start", options.StartLimit, options.StartWindowSeconds, ct);
            var provider = ClassifyProvider(http.Request.Headers.UserAgent.ToString());
            var (challenge, record) = remote.CreateChallenge(caller, site, path, width, height, waitMs, provider);
            ApplyPrivateHeaders(http.Response);
            var root = PublicRoot(http.Request);
            var encoded = Uri.EscapeDataString(challenge);
            var response = new AiRemoteStartResponse(
                ApiVersion,
                "This request created only a one-time challenge. Open confirmUrl to allocate the writable Chromium session; no TaskForge account is created automatically.",
                record.ExpiresAtUtc,
                $"{root}/api/ai/browser/confirm?challenge={encoded}",
                $"{root}/api/ai/browser/confirm?challenge={encoded}&format=html",
                new
                {
                    agentChoosesEverything = true,
                    accountCreation = "Navigate the remote browser to /register?accountType=ai and fill the real form yourself.",
                    images = "Use session Links.View for an HTML page with the live PNG, or Links.Screenshot/FullPageScreenshot for raw images. These work after login too."
                });
            return Respond(response, format);
        }).WithName("StartAiRemoteBrowser").WithTags("AI remote browser").Produces<AiRemoteStartResponse>().AllowAnonymous();

        app.MapPost("/api/ai/browser/start", async (
            HttpContext http,
            AiRemoteStartRequest request,
            AiRemoteBrowserService remote,
            AiRemoteBrowserOptions options,
            BrowserCallerResolver callers,
            RedisFixedWindowRateLimiter limiter,
            CancellationToken ct) =>
        {
            var caller = callers.Resolve(http);
            callers.ThrowIfInvalidCredential(caller);
            await EnforceRateLimit(http, limiter, caller, "ai-remote-start", options.StartLimit, options.StartWindowSeconds, ct);
            var provider = ClassifyProvider(http.Request.Headers.UserAgent.ToString());
            var (challenge, record) = remote.CreateChallenge(caller, request.Site, request.Path, request.Width, request.Height, request.WaitMs, provider);
            ApplyPrivateHeaders(http.Response);
            var root = PublicRoot(http.Request);
            var encoded = Uri.EscapeDataString(challenge);
            return Results.Ok(new AiRemoteStartResponse(
                ApiVersion,
                "Challenge created. Confirm it to allocate a writable Chromium session; no account is created automatically.",
                record.ExpiresAtUtc,
                $"{root}/api/ai/browser/confirm?challenge={encoded}",
                $"{root}/api/ai/browser/confirm?challenge={encoded}&format=html",
                new { agentChoosesEverything = true, accountCreationIsManualThroughRealUi = true }));
        }).WithName("StartAiRemoteBrowserPost").WithTags("AI remote browser").Produces<AiRemoteStartResponse>().AllowAnonymous();

        app.MapGet("/api/ai/browser/confirm", async (
            HttpContext http,
            string? challenge,
            string? format,
            AiRemoteBrowserService remote,
            AiRemoteBrowserOptions options,
            BrowserCallerResolver callers,
            RedisFixedWindowRateLimiter limiter,
            CancellationToken ct) =>
        {
            if (IsIndexingCrawler(http.Request.Headers.UserAgent.ToString()))
            {
                throw new BrowserApiException(StatusCodes.Status403Forbidden, "AI_REMOTE_INDEXING_CRAWLER_BLOCKED", "Search/indexing crawlers may read discovery but cannot allocate writable remote-browser sessions.");
            }
            var caller = callers.Resolve(http);
            callers.ThrowIfInvalidCredential(caller);
            await EnforceRateLimit(http, limiter, caller, "ai-remote-confirm", options.ConfirmLimit, options.ConfirmWindowSeconds, ct);
            var started = await remote.ConfirmAsync(challenge, ct);
            ApplyPrivateHeaders(http.Response);
            var response = BuildSessionResponse(http.Request, started.Session, started.Secret, started.Snapshot);
            return Respond(response, format, statusCode: StatusCodes.Status201Created);
        }).WithName("ConfirmAiRemoteBrowser").WithTags("AI remote browser").Produces<AiRemoteBrowserSessionResponse>(StatusCodes.Status201Created).AllowAnonymous();
    }

    private static void MapRead(WebApplication app)
    {
        app.MapGet("/api/ai/browser/s/{id:guid}", async (
            Guid id, HttpContext http, string? k, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            await LimitAction(http, options, callers, limiter, ct);
            ApplyPrivateHeaders(http.Response);
            var outcome = await remote.SnapshotAsync(id, k, includeText: true, waitMs, ct);
            return Results.Ok(BuildSessionResponse(http.Request, outcome.Session, k!, outcome.Snapshot));
        }).WithName("GetAiRemoteBrowserSession").WithTags("AI remote browser").Produces<AiRemoteBrowserSessionResponse>().AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/snapshot", async (
            Guid id, HttpContext http, string? k, bool? includeText, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            await LimitAction(http, options, callers, limiter, ct);
            ApplyPrivateHeaders(http.Response);
            var outcome = await remote.SnapshotAsync(id, k, includeText ?? true, waitMs, ct);
            return Results.Ok(BuildSessionResponse(http.Request, outcome.Session, k!, outcome.Snapshot));
        }).WithName("GetAiRemoteBrowserSnapshot").WithTags("AI remote browser").Produces<AiRemoteBrowserSessionResponse>().AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/wait", async (
            Guid id, HttpContext http, string? k, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            await LimitAction(http, options, callers, limiter, ct);
            ApplyPrivateHeaders(http.Response);
            var outcome = await remote.SnapshotAsync(id, k, includeText: true, waitMs ?? 2000, ct);
            return Results.Ok(BuildSessionResponse(http.Request, outcome.Session, k!, outcome.Snapshot));
        }).WithName("WaitAiRemoteBrowser").WithTags("AI remote browser").Produces<AiRemoteBrowserSessionResponse>().AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/screenshot", async (
            Guid id, HttpContext http, string? k, bool? fullPage, bool? annotated,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            await LimitScreenshot(http, options, callers, limiter, ct);
            ApplyPrivateHeaders(http.Response);
            var outcome = await remote.ScreenshotAsync(id, k, fullPage ?? false, annotated ?? false, ct);
            http.Response.Headers["X-TaskForge-AI-Remote-Session"] = id.ToString();
            http.Response.Headers["X-TaskForge-Render-Width"] = outcome.Screenshot.Width.ToString();
            http.Response.Headers["X-TaskForge-Render-Height"] = outcome.Screenshot.Height.ToString();
            http.Response.Headers["X-TaskForge-Full-Page"] = outcome.Screenshot.FullPage ? "true" : "false";
            http.Response.Headers["X-TaskForge-Full-Page-Truncated"] = outcome.Screenshot.FullPageTruncated ? "true" : "false";
            return Results.File(outcome.Screenshot.Bytes, "image/png", enableRangeProcessing: false);
        }).WithName("GetAiRemoteBrowserScreenshot").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/view", async (
            Guid id, HttpContext http, string? k, bool? fullPage, bool? annotated, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            await LimitAction(http, options, callers, limiter, ct);
            ApplyPrivateHeaders(http.Response);
            var outcome = await remote.SnapshotAsync(id, k, includeText: true, waitMs, ct);
            var response = BuildSessionResponse(http.Request, outcome.Session, k!, outcome.Snapshot);
            var links = response.Links;
            var image = fullPage == true ? links.FullPageScreenshot : links.Screenshot;
            if (annotated == true) image += image.Contains('?') ? "&annotated=true" : "?annotated=true";
            var html = BuildVisualHtml(response, image);
            return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
        }).WithName("ViewAiRemoteBrowserSession").WithTags("AI remote browser").AllowAnonymous();
    }

    private static void MapActions(WebApplication app)
    {
        app.MapGet("/api/ai/browser/s/{id:guid}/navigate", async (
            Guid id, HttpContext http, string? k, string? path, string? pathBase64Url, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.NavigateAsync(id, k, path, pathBase64Url, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("NavigateAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/click", async (
            Guid id, HttpContext http, string? k, string? element, int? clickCount, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.ClickAsync(id, k, element, clickCount, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("ClickAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/fill", async (
            Guid id, HttpContext http, string? k, string? element, string? value, string? valueBase64Url, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.FillAsync(id, k, element, value, valueBase64Url, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("FillAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/insert", async (
            Guid id, HttpContext http, string? k, string? element, string? value, string? valueBase64Url, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.InsertAsync(id, k, element, value, valueBase64Url, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("InsertAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/select", async (
            Guid id, HttpContext http, string? k, string? element, string? value, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.SelectAsync(id, k, element, value, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("SelectAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/hover", async (
            Guid id, HttpContext http, string? k, string? element, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.HoverAsync(id, k, element, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("HoverAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/key", async (
            Guid id, HttpContext http, string? k, string? key, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.KeyAsync(id, k, key, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("KeyAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/mouse-click", async (
            Guid id, HttpContext http, string? k, double? x, double? y, int? clickCount, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.MouseClickAsync(id, k, x, y, clickCount, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("MouseClickAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/press", async (
            Guid id, HttpContext http, string? k, string? element, string? key, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.PressAsync(id, k, element, key, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("PressAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/check", async (
            Guid id, HttpContext http, string? k, string? element, bool? @checked, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.CheckAsync(id, k, element, @checked, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("CheckAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/scroll", async (
            Guid id, HttpContext http, string? k, string? element, double? deltaX, double? deltaY, int? waitMs,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.ScrollAsync(id, k, element, deltaX, deltaY, waitMs, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("ScrollAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/back", async (
            Guid id, HttpContext http, string? k,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.BackAsync(id, k, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("BackAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/reload", async (
            Guid id, HttpContext http, string? k,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.ReloadAsync(id, k, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("ReloadAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();

        app.MapPost("/api/ai/browser/s/{id:guid}/action/{action}", async (
            Guid id, string action, HttpContext http, string? k, AiRemoteActionRequest request,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            var outcome = await remote.PerformAsync(id, k, action, request, ct);
            return Results.Ok(BuildActionResponse(http.Request, k!, outcome));
        }).WithName("PerformAiRemoteBrowserActionPost").WithTags("AI remote browser").AllowAnonymous();

        app.MapGet("/api/ai/browser/s/{id:guid}/close", async (
            Guid id, HttpContext http, string? k, string? confirm,
            AiRemoteBrowserService remote, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
        {
            EnsureGetMutations(options); await LimitAction(http, options, callers, limiter, ct); ApplyPrivateHeaders(http.Response);
            if (!string.Equals(confirm, "close", StringComparison.Ordinal))
            {
                return Results.Ok(new { message = "This is a destructive action. Repeat with confirm=close.", closeUrl = $"{PublicRoot(http.Request)}/api/ai/browser/s/{id}/close?k={Uri.EscapeDataString(k ?? string.Empty)}&confirm=close" });
            }
            await remote.CloseAsync(id, k);
            return Results.Ok(new { closed = true, sessionId = id });
        }).WithName("CloseAiRemoteBrowserGet").WithTags("AI remote browser").AllowAnonymous();
    }

    private static AiRemoteBrowserSessionResponse BuildSessionResponse(HttpRequest request, AiRemoteBrowserSession session, string secret, SiteSnapshotResponse snapshot)
        => new(
            ApiVersion,
            session.Id,
            secret,
            session.CreatedAtUtc,
            session.AbsoluteExpiresAtUtc,
            session.LastSeenAtUtc.Add(session.IdleTimeout),
            session.Site,
            session.Width,
            session.Height,
            Links(request, session.Id, secret),
            snapshot);

    private static AiRemoteBrowserActionResponse BuildActionResponse(HttpRequest request, string secret, AiRemoteActionOutcome outcome)
        => new(
            ApiVersion,
            outcome.Session.Id,
            outcome.Action,
            DateTimeOffset.UtcNow,
            Links(request, outcome.Session.Id, secret),
            outcome.Snapshot,
            outcome.Message);

    private static AiRemoteBrowserLinks Links(HttpRequest request, Guid id, string secret)
    {
        var root = PublicRoot(request);
        var basePath = $"{root}/api/ai/browser/s/{id}";
        var k = Uri.EscapeDataString(secret);
        var auth = $"k={k}";
        // A unique visual URL matters for AI web fetchers that may cache image URLs
        // more aggressively than a normal browser despite no-store response headers.
        var visualVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new AiRemoteBrowserLinks(
            $"{basePath}?{auth}",
            $"{basePath}/snapshot?{auth}",
            $"{basePath}/screenshot?{auth}&v={visualVersion}",
            $"{basePath}/screenshot?{auth}&fullPage=true&v={visualVersion}",
            $"{basePath}/screenshot?{auth}&annotated=true&v={visualVersion}",
            $"{basePath}/screenshot?{auth}&fullPage=true&annotated=true&v={visualVersion}",
            $"{basePath}/view?{auth}&v={visualVersion}",
            $"{basePath}/view?{auth}&fullPage=true&v={visualVersion}",
            $"{basePath}/view?{auth}&annotated=true&v={visualVersion}",
            $"{basePath}/view?{auth}&fullPage=true&annotated=true&v={visualVersion}",
            $"{basePath}/navigate?{auth}&path={{URL_ENCODED_RELATIVE_PATH}}",
            $"{basePath}/click?{auth}&element={{tfN-or-automationId}}",
            $"{basePath}/fill?{auth}&element={{tfN-or-automationId}}&valueBase64Url={{BASE64URL_UTF8}}",
            $"{basePath}/insert?{auth}&element={{tfN-or-automationId}}&valueBase64Url={{BASE64URL_UTF8_CHUNK}}",
            $"{basePath}/select?{auth}&element={{tfN-or-automationId}}&value={{OPTION_VALUE}}",
            $"{basePath}/press?{auth}&element={{tfN-or-automationId}}&key={{KEY}}",
            $"{basePath}/key?{auth}&key={{KEY}}",
            $"{basePath}/hover?{auth}&element={{tfN-or-automationId}}",
            $"{basePath}/mouse-click?{auth}&x={{VIEWPORT_X}}&y={{VIEWPORT_Y}}",
            $"{basePath}/check?{auth}&element={{tfN-or-automationId}}&checked=true",
            $"{basePath}/scroll?{auth}&deltaY=700",
            $"{basePath}/wait?{auth}&waitMs={{0-15000}}",
            $"{basePath}/back?{auth}",
            $"{basePath}/reload?{auth}",
            $"{basePath}/close?{auth}");
    }

    private static string BuildVisualHtml(AiRemoteBrowserSessionResponse response, string imageUrl)
    {
        var json = WebUtility.HtmlEncode(JsonSerializer.Serialize(response.Snapshot, HtmlJson));
        var image = WebUtility.HtmlEncode(imageUrl);
        var snapshot = WebUtility.HtmlEncode(response.Links.Snapshot);
        var full = WebUtility.HtmlEncode(response.Links.FullPageView);
        var current = WebUtility.HtmlEncode(response.Snapshot.Url);
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex,nofollow,noarchive"><meta name="referrer" content="no-referrer"><title>TaskForge AI remote view</title>
<style>body{font:15px/1.45 system-ui;margin:20px;background:#111;color:#eee}a{color:#9cf}img{display:block;max-width:100%;height:auto;border:1px solid #555;background:white}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#191919;padding:16px;border-radius:8px}</style></head><body>
<h1>Live TaskForge Chromium view</h1><p><strong>Current page:</strong> {{current}}</p>
<p><a href="{{snapshot}}">semantic snapshot JSON</a> · <a href="{{full}}">full-page visual view</a></p>
<img src="{{image}}" alt="Live screenshot of the current TaskForge browser session">
<h2>Semantic snapshot</h2><pre>{{json}}</pre></body></html>
""";
    }

    private static IResult Respond(object value, string? format, int statusCode = StatusCodes.Status200OK)
    {
        if (!string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(value, statusCode: statusCode);
        }
        var json = WebUtility.HtmlEncode(JsonSerializer.Serialize(value, HtmlJson));
        string? next = value switch
        {
            AiRemoteStartResponse start => start.ConfirmUrl,
            AiRemoteBrowserSessionResponse session => session.Links.View,
            _ => null
        };
        var nextHtml = string.IsNullOrWhiteSpace(next) ? string.Empty : $"<p><a href=\"{WebUtility.HtmlEncode(next)}\">Continue</a></p>";
        var html = $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"robots\" content=\"noindex,nofollow,noarchive\"><meta name=\"referrer\" content=\"no-referrer\"><title>TaskForge AI remote browser</title></head><body><h1>TaskForge AI remote browser</h1>{nextHtml}<pre>{json}</pre></body></html>";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, statusCode);
    }

    private static void ApplyPrivateHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, private";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive, nosnippet";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static void EnsureGetMutations(AiRemoteBrowserOptions options)
    {
        if (!options.AllowGetMutations)
        {
            throw new BrowserApiException(StatusCodes.Status405MethodNotAllowed, "AI_REMOTE_GET_MUTATIONS_DISABLED", "GET compatibility actions are disabled. Use the normal POST Browser API.");
        }
    }

    private static async Task LimitAction(HttpContext http, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct)
    {
        var caller = callers.Resolve(http);
        callers.ThrowIfInvalidCredential(caller);
        await EnforceRateLimit(http, limiter, caller, "ai-remote-action", options.ActionLimit, options.ActionWindowSeconds, ct);
    }

    private static async Task LimitScreenshot(HttpContext http, AiRemoteBrowserOptions options, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct)
    {
        var caller = callers.Resolve(http);
        callers.ThrowIfInvalidCredential(caller);
        await EnforceRateLimit(http, limiter, caller, "ai-remote-screenshot", options.ScreenshotLimit, options.ScreenshotWindowSeconds, ct);
    }

    private static async Task EnforceRateLimit(HttpContext http, RedisFixedWindowRateLimiter limiter, BrowserCaller caller, string operation, int limit, int seconds, CancellationToken ct)
    {
        var rateOptions = http.RequestServices.GetRequiredService<BrowserRateLimitOptions>();
        var aiMultiplier = caller.IsAuthenticated && string.Equals(caller.AccountType, "ai", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(rateOptions.AuthenticatedAiMultiplier, 1, 20)
            : 1;
        var effectiveLimit = Math.Min(1_000_000, Math.Max(1, limit) * aiMultiplier);
        if (aiMultiplier > 1)
        {
            http.Response.Headers["X-TaskForge-AI-Rate-Multiplier"] = aiMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var decision = await limiter.CheckAsync(operation, caller.OwnerKey, caller.NetworkKey, effectiveLimit, Math.Max(1, seconds), ct);
        var resetAfterSeconds = Math.Max(0, (int)Math.Ceiling((decision.ResetAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
        var resetUnix = decision.ResetAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["RateLimit-Limit"] = decision.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["RateLimit-Remaining"] = decision.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["RateLimit-Reset"] = resetAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["RateLimit-Policy"] = $"{decision.Limit};w={decision.WindowSeconds}";
        http.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-RateLimit-Reset"] = resetUnix;
        if (!decision.Allowed)
        {
            http.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new BrowserApiException(StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "Слишком много AI remote-browser запросов. Повторите позже.", decision.RetryAfterSeconds);
        }
    }

    private static string ClassifyProvider(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        if (ua.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) || ua.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (ua.Contains("Claude", StringComparison.OrdinalIgnoreCase) || ua.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)) return "anthropic";
        if (ua.Contains("Perplexity", StringComparison.OrdinalIgnoreCase)) return "perplexity";
        if (ua.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || ua.Contains("Google", StringComparison.OrdinalIgnoreCase)) return "google";
        if (ua.Contains("Mistral", StringComparison.OrdinalIgnoreCase)) return "mistral";
        if (ua.Contains("curl", StringComparison.OrdinalIgnoreCase) || ua.Contains("wget", StringComparison.OrdinalIgnoreCase)) return "cli";
        return "generic";
    }

    private static bool IsIndexingCrawler(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        foreach (var interactive in new[] { "ChatGPT-User", "Claude-User", "Perplexity-User", "Gemini-User" })
        {
            if (ua.Contains(interactive, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return new[]
        {
            "Claude-SearchBot", "ClaudeBot", "GPTBot", "OAI-SearchBot", "Googlebot", "Google-Extended", "bingbot", "YandexBot",
            "DuckDuckBot", "MJ12bot", "AhrefsBot", "SemrushBot", "PetalBot", "Bytespider", "CCBot", "Amazonbot"
        }.Any(bot => ua.Contains(bot, StringComparison.OrdinalIgnoreCase));
    }

    private static string PublicRoot(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
        return $"{scheme}://{host}".TrimEnd('/');
    }
}
