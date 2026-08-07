using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;
using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Infrastructure;
using TaskForge.Browser.Api.Diagnostics;
using TaskForge.Browser.Api.Security;
using TaskForge.Browser.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddTaskForgeDebugDiagnostics("browser-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "browser-api");

var browserOptions = builder.Configuration.GetSection("Browser").Get<BrowserOptions>() ?? new BrowserOptions();
var rateOptions = builder.Configuration.GetSection("BrowserRateLimits").Get<BrowserRateLimitOptions>() ?? new BrowserRateLimitOptions();
builder.Services.AddSingleton(browserOptions);
builder.Services.AddSingleton(rateOptions);
builder.Services.AddSingleton<BrowserUrlPolicy>();
builder.Services.AddSingleton<BrowserCallerResolver>();
builder.Services.AddSingleton<RedisFixedWindowRateLimiter>();
builder.Services.AddSingleton<BrowserResponseCache>();
builder.Services.AddSingleton<BrowserCapacityGate>();
builder.Services.AddSingleton<BrowserRuntime>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BrowserRuntime>());
builder.Services.AddSingleton<BrowserPageFactory>();
builder.Services.AddSingleton<SnapshotBuilder>();
builder.Services.AddSingleton<BrowserScreenshotService>();
builder.Services.AddSingleton<SiteInspectionService>();
builder.Services.AddSingleton<BrowserSessionRegistry>();
builder.Services.AddHostedService<BrowserSessionCleanupService>();
builder.Services.AddSingleton<SiteRouteCatalog>();
builder.Services.AddSingleton<DiscoveryDocumentService>();

builder.Services.AddCors(options => options.AddPolicy("public-browser-api", policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("openapi", new OpenApiInfo
    {
        Title = "TaskForge Browser API",
        Version = "1.1",
        Description = "Public, rate-limited APIs for semantic snapshots, visual renders and interactive Chromium sessions restricted to TaskForge origins."
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Optional ordinary TaskForge access token. Anonymous access is supported."
    });
});
builder.Services.AddHealthChecks().AddCheck<BrowserRuntimeHealthCheck>("chromium");

var app = builder.Build();

ValidateConfiguration(app.Configuration, app.Environment, browserOptions, rateOptions);

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
});
app.UseTaskForgeDebugRequestLogging("browser-api");
app.UseCors("public-browser-api");
app.Use(async (http, next) =>
{
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    http.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";

    var path = http.Request.Path.Value ?? string.Empty;
    if (path is "/.well-known/taskforge-ai.json" or "/llms.txt" or "/api/browser/openapi.json")
    {
        http.Response.Headers.CacheControl = "public, max-age=300";
    }
    else if (path is "/api/site/info" or "/api/site/routes")
    {
        http.Response.Headers.CacheControl = "public, max-age=60";
    }

    await next();
});
app.Use(async (http, next) =>
{
    try
    {
        await next();
    }
    catch (BrowserApiException ex)
    {
        if (http.Response.HasStarted) throw;
        http.Response.Headers.CacheControl = "no-store";
        if (ex.RetryAfterSeconds is > 0) http.Response.Headers.RetryAfter = ex.RetryAfterSeconds.Value.ToString();
        http.Response.StatusCode = ex.StatusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(new ApiError(ex.Message, ex.Code, http.TraceIdentifier, ex.RetryAfterSeconds));
    }
    catch (PlaywrightException ex)
    {
        if (http.Response.HasStarted) throw;
        http.Response.Headers.CacheControl = "no-store";
        app.Logger.LogInformation(ex, "Chromium request failed. trace={TraceId}", http.TraceIdentifier);
        http.Response.StatusCode = StatusCodes.Status502BadGateway;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(new ApiError("Chromium не смог отобразить страницу TaskForge.", "BROWSER_RENDER_FAILED", http.TraceIdentifier));
    }
    catch (Exception ex)
    {
        if (http.Response.HasStarted) throw;
        http.Response.Headers.CacheControl = "no-store";
        app.Logger.LogError(ex, "Unhandled browser-api error. trace={TraceId}", http.TraceIdentifier);
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(new ApiError("Внутренняя ошибка Browser API.", "INTERNAL_ERROR", http.TraceIdentifier));
    }
});
app.UseSwagger(options => options.RouteTemplate = "api/browser/{documentName}.json");

app.MapHealthChecks("/health").ExcludeFromDescription();

app.MapGet("/.well-known/taskforge-ai.json", (HttpRequest request, DiscoveryDocumentService discovery) =>
    Results.Json(discovery.BuildDiscovery(request)))
    .WithName("GetTaskForgeAiDiscovery")
    .WithTags("Discovery")
    .AllowAnonymous();

app.MapGet("/llms.txt", (HttpRequest request, DiscoveryDocumentService discovery) =>
    Results.Text(discovery.BuildLlmsText(request), "text/plain; charset=utf-8"))
    .WithName("GetLlmsInstructions")
    .WithTags("Discovery")
    .AllowAnonymous();

app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nAllow: /\n", "text/plain; charset=utf-8"))
    .ExcludeFromDescription();

app.MapGet("/api/site/info", async (
    HttpContext http,
    BrowserUrlPolicy policy,
    BrowserOptions options,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "metadata", limits.MetadataLimit, limits.MetadataWindowSeconds, ct);
    var root = PublicRoot(http.Request);
    return Results.Ok(new SiteInfoResponse(
        "TaskForge",
        "1.1",
        policy.DefaultSite,
        policy.Sites.ToDictionary(x => x.Key, x => x.Value.AbsoluteUri.TrimEnd('/')),
        true,
        true,
        true,
        $"{root}/.well-known/taskforge-ai.json",
        $"{root}/llms.txt",
        $"{root}/api/browser/openapi.json",
        $"{root}/api/auth/register",
        $"{root}/api/auth/login",
        $"{root}/api/site/snapshot",
        $"{root}/api/site/render",
        $"{root}/api/browser/sessions",
        new
        {
            viewport = new { minWidth = options.MinViewportWidth, maxWidth = options.MaxViewportWidth, minHeight = options.MinViewportHeight, maxHeight = options.MaxViewportHeight },
            maxFullPageHeight = options.MaxFullPageHeight,
            maxScreenshotPixels = options.MaxScreenshotPixels,
            maxArtifactResponseBytes = options.MaxArtifactResponseBytes,
            maxSnapshotElements = options.MaxSnapshotElements,
            maxSnapshotTextCharacters = options.MaxSnapshotTextCharacters,
            maxAriaSnapshotCharacters = options.MaxAriaSnapshotCharacters,
            activeSessions = options.MaxActiveSessions,
            anonymousSessionsPerOwner = options.MaxAnonymousSessionsPerOwner,
            authenticatedSessionsPerOwner = options.MaxAuthenticatedSessionsPerOwner,
            sessionIdleMinutes = options.SessionIdleMinutes,
            sessionAbsoluteMinutes = options.SessionAbsoluteMinutes
        }));
})
.WithName("GetSiteInfo")
.WithTags("Site inspection")
.AllowAnonymous();

app.MapGet("/api/site/routes", async (
    HttpContext http,
    string? site,
    SiteRouteCatalog routes,
    BrowserUrlPolicy policy,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "metadata", limits.MetadataLimit, limits.MetadataWindowSeconds, ct);
    string? normalizedSite = null;
    if (!string.IsNullOrWhiteSpace(site))
    {
        normalizedSite = policy.ResolveSite(site).Site;
    }

    return Results.Ok(new { routes = routes.GetRoutes(normalizedSite) });
})
.WithName("GetKnownSiteRoutes")
.WithTags("Site inspection")
.AllowAnonymous();

app.MapGet("/api/site/snapshot", async (
    HttpContext http,
    string? site,
    string? path,
    int? width,
    int? height,
    int? waitMs,
    bool? includeText,
    SiteInspectionService inspections,
    BrowserOptions options,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "snapshot", limits.SnapshotLimit, limits.SnapshotWindowSeconds, ct);
    var result = await inspections.SnapshotAsync(site, path, width ?? 1440, height ?? 900, NormalizeWait(waitMs, options), includeText ?? true, caller, ct);
    ApplyArtifactHeaders(http.Response, result.CacheHit, false, false, false, result.Snapshot.Viewport.Width, result.Snapshot.Viewport.Height, options, caller);
    return Results.Ok(result.Snapshot);
})
.WithName("GetSiteSnapshot")
.WithTags("Site inspection")
.AllowAnonymous();

app.MapGet("/api/site/render", async (
    HttpContext http,
    string? site,
    string? path,
    int? width,
    int? height,
    int? waitMs,
    bool? fullPage,
    bool? annotated,
    SiteInspectionService inspections,
    BrowserOptions options,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "render", limits.RenderLimit, limits.RenderWindowSeconds, ct);
    var result = await inspections.RenderPngAsync(site, path, width ?? 1440, height ?? 900, NormalizeWait(waitMs, options), fullPage ?? true, annotated ?? false, caller, ct);
    ApplyArtifactHeaders(http.Response, result.CacheHit, result.FullPage, result.FullPageTruncated, result.Annotated, result.Width, result.Height, options, caller);
    return Results.File(result.Bytes, result.ContentType, enableRangeProcessing: false);
})
.WithName("RenderSitePng")
.WithTags("Site inspection")
.AllowAnonymous();

app.MapGet("/api/site/render.pdf", async (
    HttpContext http,
    string? site,
    string? path,
    int? width,
    int? height,
    int? waitMs,
    bool? fullPage,
    bool? annotated,
    SiteInspectionService inspections,
    BrowserOptions options,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "render", limits.RenderLimit, limits.RenderWindowSeconds, ct);
    var result = await inspections.RenderPdfAsync(site, path, width ?? 1440, height ?? 900, NormalizeWait(waitMs, options), fullPage ?? true, annotated ?? false, caller, ct);
    ApplyArtifactHeaders(http.Response, result.CacheHit, result.FullPage, result.FullPageTruncated, result.Annotated, result.Width, result.Height, options, caller);
    return Results.File(result.Bytes, result.ContentType, enableRangeProcessing: false);
})
.WithName("RenderSitePdf")
.WithTags("Site inspection")
.AllowAnonymous();

app.MapPost("/api/browser/sessions", async (
    HttpContext http,
    CreateBrowserSessionRequest request,
    BrowserSessionRegistry sessions,
    BrowserOptions options,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "session-create", limits.SessionCreateLimit, limits.SessionCreateWindowSeconds, ct);
    var created = await sessions.CreateAsync(request, caller, ct);
    http.Response.Headers["X-TaskForge-Browser-Session-Token"] = created.RawToken;
    http.Response.Headers.CacheControl = "no-store";
    var basePath = $"/api/browser/sessions/{created.Session.Id}";
    return Results.Created(basePath, new CreateBrowserSessionResponse(
        created.Session.Id,
        created.RawToken,
        created.Session.Handle.Site,
        created.Snapshot.Url,
        created.Session.Handle.ReadOnly,
        caller.IsAuthenticated,
        caller.IsAuthenticated ? caller.AccountType : "anonymous",
        created.Session.CreatedAtUtc,
        created.Session.AbsoluteExpiresAtUtc,
        created.Session.Handle.Width,
        created.Session.Handle.Height,
        $"{basePath}/snapshot",
        $"{basePath}/screenshot",
        new { name = "X-TaskForge-Browser-Session-Token", value = created.RawToken },
        created.Snapshot));
})
.WithName("CreateBrowserSession")
.WithTags("Interactive browser")
.AllowAnonymous();

app.MapGet("/api/browser/sessions/{id:guid}/snapshot", async (
    Guid id,
    HttpContext http,
    bool? includeText,
    BrowserSessionRegistry sessions,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "session-action", limits.SessionActionLimit, limits.SessionActionWindowSeconds, ct);
    http.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await sessions.SnapshotAsync(id, SessionToken(http), caller, includeText ?? true, ct));
})
.WithName("GetBrowserSessionSnapshot")
.WithTags("Interactive browser")
.AllowAnonymous();

app.MapGet("/api/browser/sessions/{id:guid}/screenshot", async (
    Guid id,
    HttpContext http,
    bool? fullPage,
    bool? annotated,
    BrowserSessionRegistry sessions,
    BrowserOptions options,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "session-action", limits.SessionActionLimit, limits.SessionActionWindowSeconds, ct);
    var capture = await sessions.ScreenshotAsync(id, SessionToken(http), caller, fullPage ?? false, annotated ?? false, ct);
    ApplySessionArtifactHeaders(http.Response, capture);
    return Results.File(capture.Bytes, "image/png", enableRangeProcessing: false);
})
.WithName("GetBrowserSessionScreenshot")
.WithTags("Interactive browser")
.AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/navigate", async (Guid id, HttpContext http, NavigateBrowserSessionRequest request, bool? includeSnapshot, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.NavigateAsync(id, SessionToken(http), caller, request, includeSnapshot ?? true, ct)))
    .WithName("NavigateBrowserSession").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/click", async (Guid id, HttpContext http, ClickBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.ClickAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("ClickBrowserElement").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/fill", async (Guid id, HttpContext http, FillBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.FillAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("FillBrowserElement").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/press", async (Guid id, HttpContext http, PressBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.PressAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("PressBrowserElementKey").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/select", async (Guid id, HttpContext http, SelectBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.SelectAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("SelectBrowserElementOption").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/hover", async (Guid id, HttpContext http, HoverBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.HoverAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("HoverBrowserElement").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/check", async (Guid id, HttpContext http, CheckBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.CheckAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("SetBrowserElementChecked").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/scroll", async (Guid id, HttpContext http, ScrollBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.ScrollAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("ScrollBrowserSession").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/back", async (Guid id, HttpContext http, bool? includeSnapshot, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.BackAsync(id, SessionToken(http), caller, includeSnapshot ?? true, ct)))
    .WithName("GoBackBrowserSession").WithTags("Interactive browser").AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/reload", async (Guid id, HttpContext http, bool? includeSnapshot, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.ReloadAsync(id, SessionToken(http), caller, includeSnapshot ?? true, ct)))
    .WithName("ReloadBrowserSession").WithTags("Interactive browser").AllowAnonymous();

app.MapDelete("/api/browser/sessions/{id:guid}", async (Guid id, HttpContext http, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
{
    var caller = callers.Resolve(http);
    callers.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "session-action", limits.SessionActionLimit, limits.SessionActionWindowSeconds, ct);
    await sessions.CloseAsync(id, SessionToken(http), caller);
    return Results.NoContent();
})
.WithName("CloseBrowserSession")
.WithTags("Interactive browser")
.AllowAnonymous();

app.Run();

static async Task<IResult> SessionAction(
    HttpContext http,
    BrowserCallerResolver callers,
    RedisFixedWindowRateLimiter limiter,
    BrowserRateLimitOptions limits,
    CancellationToken cancellationToken,
    Func<BrowserCaller, Task<BrowserActionResponse>> action)
{
    var caller = callers.Resolve(http);
    callers.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "session-action", limits.SessionActionLimit, limits.SessionActionWindowSeconds, cancellationToken);
    http.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await action(caller));
}

static async Task EnforceRateLimit(
    HttpContext http,
    RedisFixedWindowRateLimiter limiter,
    BrowserCaller caller,
    string bucket,
    int limit,
    int windowSeconds,
    CancellationToken cancellationToken)
{
    var decision = await limiter.CheckAsync(bucket, caller.OwnerKey, caller.NetworkKey, limit, windowSeconds, cancellationToken);
    http.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString();
    http.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString();
    if (!decision.Allowed)
    {
        http.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
        throw new BrowserApiException(StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "Слишком много запросов к Browser API. Подождите и повторите попытку.", decision.RetryAfterSeconds);
    }
}

static string? SessionToken(HttpContext http)
    => http.Request.Headers["X-TaskForge-Browser-Session-Token"].FirstOrDefault();

static int NormalizeWait(int? wait, BrowserOptions options)
    => System.Math.Clamp(wait ?? options.DefaultWaitMilliseconds, 0, options.MaxWaitMilliseconds);

static void ApplySessionArtifactHeaders(HttpResponse response, BrowserScreenshot capture)
{
    response.Headers.CacheControl = "no-store";
    response.Headers["X-TaskForge-Render-Width"] = capture.Width.ToString();
    response.Headers["X-TaskForge-Render-Height"] = capture.Height.ToString();
    response.Headers["X-TaskForge-Full-Page"] = capture.FullPage ? "true" : "false";
    if (capture.FullPageTruncated) response.Headers["X-TaskForge-Full-Page-Truncated"] = "true";
    response.Headers["X-TaskForge-Annotated"] = capture.Annotated ? "true" : "false";
}

static void ApplyArtifactHeaders(HttpResponse response, bool cacheHit, bool fullPage, bool fullPageTruncated, bool annotated, int width, int height, BrowserOptions options, BrowserCaller caller)
{
    response.Headers["X-TaskForge-Cache"] = cacheHit ? "HIT" : "MISS";
    response.Headers["X-TaskForge-Render-Width"] = width.ToString();
    response.Headers["X-TaskForge-Render-Height"] = height.ToString();
    response.Headers["X-TaskForge-Full-Page"] = fullPage ? "true" : "false";
    if (fullPageTruncated) response.Headers["X-TaskForge-Full-Page-Truncated"] = "true";
    response.Headers["X-TaskForge-Annotated"] = annotated ? "true" : "false";
    response.Headers.Vary = "Authorization";
    response.Headers.CacheControl = caller.IsAuthenticated ? "private, no-store" : $"public, max-age={options.PublicCacheSeconds}";
}

static string PublicRoot(HttpRequest request)
{
    var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
    var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
    return $"{scheme}://{host}".TrimEnd('/');
}

static void ValidateConfiguration(
    IConfiguration configuration,
    IHostEnvironment environment,
    BrowserOptions options,
    BrowserRateLimitOptions rateOptions)
{
    var sites = options.GetSites();
    if (sites.Count == 0) throw new InvalidOperationException("Browser:Sites must contain at least one valid HTTP(S) origin.");
    if (!sites.ContainsKey(options.DefaultSite))
        throw new InvalidOperationException($"Browser:DefaultSite '{options.DefaultSite}' is not present in Browser:Sites.");

    EnsureRange(options.NavigationTimeoutSeconds, 1, 120, "Browser:NavigationTimeoutSeconds");
    EnsureRange(options.ActionTimeoutSeconds, 1, 60, "Browser:ActionTimeoutSeconds");
    EnsureRange(options.DefaultWaitMilliseconds, 0, 10000, "Browser:DefaultWaitMilliseconds");
    EnsureRange(options.MaxWaitMilliseconds, 0, 30000, "Browser:MaxWaitMilliseconds");
    if (options.DefaultWaitMilliseconds > options.MaxWaitMilliseconds)
        throw new InvalidOperationException("Browser:DefaultWaitMilliseconds cannot exceed Browser:MaxWaitMilliseconds.");

    EnsureRange(options.MinViewportWidth, 240, 4096, "Browser:MinViewportWidth");
    EnsureRange(options.MaxViewportWidth, options.MinViewportWidth, 4096, "Browser:MaxViewportWidth");
    EnsureRange(options.MinViewportHeight, 240, 4096, "Browser:MinViewportHeight");
    EnsureRange(options.MaxViewportHeight, options.MinViewportHeight, 4096, "Browser:MaxViewportHeight");
    EnsureRange(options.MaxFullPageHeight, options.MaxViewportHeight, 50000, "Browser:MaxFullPageHeight");
    EnsureRange(options.MaxScreenshotPixels, 1000000, 100000000, "Browser:MaxScreenshotPixels");
    EnsureRange(options.MaxSnapshotElements, 1, 5000, "Browser:MaxSnapshotElements");
    EnsureRange(options.MaxSnapshotTextCharacters, 1000, 1000000, "Browser:MaxSnapshotTextCharacters");
    EnsureRange(options.MaxAriaSnapshotCharacters, 1000, 1000000, "Browser:MaxAriaSnapshotCharacters");
    EnsureRange(options.AriaSnapshotDepth, 1, 50, "Browser:AriaSnapshotDepth");
    EnsureRange(options.MaxEventEntries, 20, 1000, "Browser:MaxEventEntries");
    EnsureRange(options.MaxConcurrentOperations, 1, 64, "Browser:MaxConcurrentOperations");
    EnsureRange(options.MaxActiveSessions, 1, 256, "Browser:MaxActiveSessions");
    EnsureRange(options.MaxAnonymousSessionsPerOwner, 1, options.MaxActiveSessions, "Browser:MaxAnonymousSessionsPerOwner");
    EnsureRange(options.MaxAuthenticatedSessionsPerOwner, 1, options.MaxActiveSessions, "Browser:MaxAuthenticatedSessionsPerOwner");
    EnsureRange(options.SessionIdleMinutes, 1, 120, "Browser:SessionIdleMinutes");
    EnsureRange(options.SessionAbsoluteMinutes, options.SessionIdleMinutes, 480, "Browser:SessionAbsoluteMinutes");
    EnsureRange(options.PublicCacheSeconds, 0, 3600, "Browser:PublicCacheSeconds");
    EnsureRange(options.MaxCachedArtifactBytes, 65536, 33554432, "Browser:MaxCachedArtifactBytes");
    EnsureRange(options.MaxArtifactResponseBytes, 1048576, 134217728, "Browser:MaxArtifactResponseBytes");
    if (options.MaxCachedArtifactBytes > options.MaxArtifactResponseBytes)
        throw new InvalidOperationException("Browser:MaxCachedArtifactBytes cannot exceed Browser:MaxArtifactResponseBytes.");

    if (string.IsNullOrWhiteSpace(options.UserAgent) || options.UserAgent.Length > 512)
        throw new InvalidOperationException("Browser:UserAgent must be between 1 and 512 characters.");

    EnsureRateRule(rateOptions.MetadataLimit, rateOptions.MetadataWindowSeconds, "Metadata");
    EnsureRateRule(rateOptions.SnapshotLimit, rateOptions.SnapshotWindowSeconds, "Snapshot");
    EnsureRateRule(rateOptions.RenderLimit, rateOptions.RenderWindowSeconds, "Render");
    EnsureRateRule(rateOptions.SessionCreateLimit, rateOptions.SessionCreateWindowSeconds, "SessionCreate");
    EnsureRateRule(rateOptions.SessionActionLimit, rateOptions.SessionActionWindowSeconds, "SessionAction");
    EnsureRange(rateOptions.NetworkMultiplier, 1, 100, "BrowserRateLimits:NetworkMultiplier");

    if (environment.IsProduction())
    {
        var jwt = configuration["Jwt:Key"] ?? configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(jwt) || jwt.Length < 48 || jwt.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production browser-api requires the same strong JWT key as identity-api.");
        if (sites.Values.Any(x => x.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Production Browser:Sites origins must use HTTPS.");
        if (options.GetAllowedExternalOrigins().Any(x => x.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Production Browser:AllowedExternalOrigins entries must use HTTPS.");
        if (!rateOptions.Enabled)
            throw new InvalidOperationException("Production browser-api requires BrowserRateLimits:Enabled=true.");
        if (!configuration.GetValue("Cache:Enabled", true))
            throw new InvalidOperationException("Production browser-api requires Cache:Enabled=true so Redis-backed limits and artifact caching are available.");

        var redis = configuration.GetConnectionString("Redis")
                    ?? configuration["Redis:ConnectionString"]
                    ?? configuration["Cache:RedisConnection"];
        if (string.IsNullOrWhiteSpace(redis))
            throw new InvalidOperationException("Production browser-api requires a Redis connection string.");
    }
}

static void EnsureRateRule(int limit, int windowSeconds, string name)
{
    EnsureRange(limit, 1, 1000000, $"BrowserRateLimits:{name}Limit");
    EnsureRange(windowSeconds, 1, 86400, $"BrowserRateLimits:{name}WindowSeconds");
}

static void EnsureRange(int value, int min, int max, string name)
{
    if (value < min || value > max)
        throw new InvalidOperationException($"{name} must be in range {min}..{max}.");
}

static void EnsureRange(long value, long min, long max, string name)
{
    if (value < min || value > max)
        throw new InvalidOperationException($"{name} must be in range {min}..{max}.");
}
