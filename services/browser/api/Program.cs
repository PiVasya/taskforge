using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Infrastructure;
using TaskForge.Browser.Api.OpenApi;
using TaskForge.Browser.Api.Diagnostics;
using TaskForge.Browser.Api.Endpoints;
using TaskForge.Browser.Api.Security;
using TaskForge.Browser.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddTaskForgeDebugDiagnostics("browser-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "browser-api");
builder.Services.AddHttpClient("support-bot", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri((cfg["Services:SupportBot"] ?? cfg["AiAccessTelemetry:SupportBotBaseUrl"] ?? "http://support-bot:8080").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddValidation();

var browserOptions = builder.Configuration.GetSection("Browser").Get<BrowserOptions>() ?? new BrowserOptions();
var rateOptions = builder.Configuration.GetSection("BrowserRateLimits").Get<BrowserRateLimitOptions>() ?? new BrowserRateLimitOptions();
var aiRemoteOptions = builder.Configuration.GetSection("AiRemoteBrowser").Get<AiRemoteBrowserOptions>() ?? new AiRemoteBrowserOptions();
builder.Services.AddSingleton(browserOptions);
builder.Services.AddSingleton(rateOptions);
builder.Services.AddSingleton(aiRemoteOptions);
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
builder.Services.AddSingleton<AiRemoteBrowserSessionStore>();
builder.Services.AddSingleton<AiRemoteBrowserService>();
builder.Services.AddHostedService<AiRemoteBrowserCleanupService>();
builder.Services.AddSingleton<SiteRouteCatalog>();
builder.Services.AddSingleton<DiscoveryDocumentService>();
builder.Services.AddSingleton<AgentAccessService>();
builder.Services.AddSingleton<PublicAgentArtifactStore>();
builder.Services.AddSingleton<PublicAgentCaptureService>();
builder.Services.AddSingleton<BrowserOpenApiDocumentEnhancer>();
builder.Services.AddSingleton<AiAccessTelemetryReporter>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AiAccessTelemetryReporter>());

builder.Services.AddCors(options => options.AddPolicy("public-browser-api", policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
        .WithExposedHeaders(
            "RateLimit-Limit", "RateLimit-Remaining", "RateLimit-Reset", "RateLimit-Policy", "Retry-After",
            "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset", "X-TaskForge-AI-Rate-Multiplier",
            "X-TaskForge-Browser-Session-Token", "X-TaskForge-Agent-Artifact-Id", "X-TaskForge-Agent-Artifact-Expires",
            "X-TaskForge-Capture-Cache", "X-TaskForge-Cache", "X-TaskForge-Snapshot-Version", "X-TaskForge-Render-Width", "X-TaskForge-Render-Height",
            "X-TaskForge-Full-Page", "X-TaskForge-Full-Page-Truncated", "X-TaskForge-Annotated", "X-TaskForge-AI-Remote-Session")));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("openapi", new OpenApiInfo
    {
        Title = "TaskForge.by Browser and Agent API",
        Version = "1.3",
        Description = "Public, rate-limited TaskForge.by APIs for semantic snapshots, visual renders, controlled interactive Chromium sessions and a low-level GET-compatible remote-browser adapter for restricted AI clients. All browser controls are restricted to configured TaskForge origins."
    });
    options.AddSecurityDefinition("BrowserSessionToken", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-TaskForge-Browser-Session-Token",
        Description = "Session token returned by POST /api/browser/sessions. Required for every request to that session."
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
ValidateAiRemoteConfiguration(aiRemoteOptions, browserOptions);
var effectiveRemoteIdleMinutes = aiRemoteOptions.GetEffectiveSessionIdleMinutes(browserOptions);
var effectiveRemoteAbsoluteMinutes = aiRemoteOptions.GetEffectiveSessionAbsoluteMinutes(browserOptions);
if (effectiveRemoteIdleMinutes != aiRemoteOptions.SessionIdleMinutes
    || effectiveRemoteAbsoluteMinutes != aiRemoteOptions.SessionAbsoluteMinutes)
{
    app.Logger.LogWarning(
        "AiRemoteBrowser session lifetime is clamped by Browser session limits: configured idle={ConfiguredIdle}m absolute={ConfiguredAbsolute}m, effective idle={EffectiveIdle}m absolute={EffectiveAbsolute}m.",
        aiRemoteOptions.SessionIdleMinutes,
        aiRemoteOptions.SessionAbsoluteMinutes,
        effectiveRemoteIdleMinutes,
        effectiveRemoteAbsoluteMinutes);
}

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
    if (path is "/.well-known/taskforge-ai.json" or "/.well-known/taskforge-ai-browser.json" or "/llms.txt" or "/api/browser/openapi.json" or "/ai-access" or "/ai-browser" or "/sitemap.xml")
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
    if (!AiAccessTelemetryReporter.IsTrackedPath(http.Request.Path))
    {
        await next();
        return;
    }

    var started = Stopwatch.GetTimestamp();
    await next();
    var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    http.RequestServices.GetRequiredService<AiAccessTelemetryReporter>().Capture(http, elapsedMs);
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
        await http.Response.WriteAsJsonAsync(new ApiError(ex.Message, ex.Code, http.TraceIdentifier, ex.RetryAfterSeconds, ex.Details));
    }
    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
    {
        // A crawler/browser client may enforce a shorter deadline than Browser API.
        // Do not report a disconnected caller as a server-side 500.
        app.Logger.LogInformation("Browser API request canceled by client. trace={TraceId} path={Path}", http.TraceIdentifier, http.Request.Path);
        if (!http.Response.HasStarted) http.Response.StatusCode = 499;
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
app.Use(async (http, next) =>
{
    if (!http.Request.Path.Equals("/api/browser/openapi.json", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var originalBody = http.Response.Body;
    await using var buffer = new MemoryStream();
    http.Response.Body = buffer;
    try
    {
        await next();
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var source = await reader.ReadToEndAsync();
        var output = http.Response.StatusCode is >= 200 and < 300 && source.Length > 0
            ? http.RequestServices.GetRequiredService<BrowserOpenApiDocumentEnhancer>().Enhance(source)
            : source;
        var bytes = Encoding.UTF8.GetBytes(output);
        http.Response.Body = originalBody;
        http.Response.ContentLength = bytes.Length;
        http.Response.ContentType = "application/json; charset=utf-8";
        await originalBody.WriteAsync(bytes);
    }
    finally
    {
        http.Response.Body = originalBody;
    }
});
app.UseSwagger(options => options.RouteTemplate = "api/browser/{documentName}.json");

app.MapHealthChecks("/health").ExcludeFromDescription();

app.MapAiRemoteBrowserEndpoints();

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

app.MapGet("/robots.txt", (HttpRequest request) =>
{
    var sitemap = $"Sitemap: {PublicRoot(request)}/sitemap.xml";
    var text = string.Join("\n", new[]
    {
        "User-agent: MJ12bot",
        "Disallow: /ai-artifacts/",
        "Disallow: /api/ai/browser/confirm",
        "Disallow: /api/ai/browser/s/",
        "",
        "User-agent: AhrefsBot",
        "Disallow: /ai-artifacts/",
        "Disallow: /api/ai/browser/confirm",
        "Disallow: /api/ai/browser/s/",
        "",
        "User-agent: SemrushBot",
        "Disallow: /ai-artifacts/",
        "Disallow: /api/ai/browser/confirm",
        "Disallow: /api/ai/browser/s/",
        "",
        "User-agent: Googlebot",
        "Disallow: /ai-artifacts/",
        "Disallow: /api/ai/browser/confirm",
        "Disallow: /api/ai/browser/s/",
        "",
        "User-agent: bingbot",
        "Disallow: /ai-artifacts/",
        "Disallow: /api/ai/browser/confirm",
        "Disallow: /api/ai/browser/s/",
        "",
        "User-agent: YandexBot",
        "Disallow: /ai-artifacts/",
        "Disallow: /api/ai/browser/confirm",
        "Disallow: /api/ai/browser/s/",
        "",
        "User-agent: DuckDuckBot",
        "Disallow: /ai-artifacts/",
        "Disallow: /api/ai/browser/confirm",
        "Disallow: /api/ai/browser/s/",
        "",
        "User-agent: *",
        "Allow: /",
        sitemap,
        ""
    });
    return Results.Text(text, "text/plain; charset=utf-8");
})
.ExcludeFromDescription();

app.MapGet("/ai-access", (HttpRequest request, AgentAccessService access) =>
    Results.Content(access.BuildIndexHtml(request), "text/html; charset=utf-8"))
    .WithName("GetAgentAccessIndex")
    .WithTags("Discovery")
    .AllowAnonymous();

app.MapGet("/sitemap.xml", (HttpRequest request, AgentAccessService access) =>
    Results.Content(access.BuildSitemapXml(request), "application/xml; charset=utf-8"))
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
        "TaskForge.by",
        "1.3",
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
        new SiteApiLimits(
            new SiteViewportLimits(options.MinViewportWidth, options.MaxViewportWidth, options.MinViewportHeight, options.MaxViewportHeight),
            options.MaxFullPageHeight,
            options.MaxScreenshotPixels,
            options.MaxArtifactResponseBytes,
            options.MaxSnapshotElements,
            options.MaxSnapshotTextCharacters,
            options.MaxAriaSnapshotCharacters,
            options.MaxActiveSessions,
            options.MaxAnonymousSessionsPerOwner,
            options.MaxAuthenticatedSessionsPerOwner,
            options.SessionIdleMinutes,
            options.SessionAbsoluteMinutes,
            options.AgentArtifactTtlSeconds,
            options.CaptureTimeoutSeconds,
            options.CaptureCacheSeconds,
            options.RecommendedCaptureConcurrency,
            System.Math.Clamp(limits.AuthenticatedAiMultiplier, 1, 20),
            "2.2",
            new[] { "RateLimit-Limit", "RateLimit-Remaining", "RateLimit-Reset", "RateLimit-Policy", "X-RateLimit-Reset", "X-TaskForge-AI-Rate-Multiplier", "Retry-After" })));
})
.WithName("GetSiteInfo")
.WithTags("Site inspection")
.Produces<SiteInfoResponse>(StatusCodes.Status200OK)
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

    return Results.Ok(new SiteRoutesResponse(routes.GetRoutes(normalizedSite)));
})
.WithName("GetKnownSiteRoutes")
.WithTags("Site inspection")
.Produces<SiteRoutesResponse>(StatusCodes.Status200OK)
.AllowAnonymous();

app.MapGet("/api/site/agent/playbook", (HttpRequest request, BrowserOptions options, BrowserRateLimitOptions rateLimits) =>
{
    var root = PublicRoot(request);
    return Results.Ok(new
    {
        version = "1.1",
        purpose = "Discover TaskForge, register an AI-marked ordinary account, navigate the real site with Chromium, solve assignments, and reconcile submissions against authoritative TaskForge APIs.",
        principles = new[]
        {
            "Use Browser API for discovery, navigation, visual inspection and UI-only flows; use ordinary authenticated TaskForge APIs as the authoritative source for submissions, attempts and verdicts when the client can send POST/GET requests.",
            "Prefer one interactive session over repeated public captures once authenticated.",
            "Use stable automationId directly in Browser action endpoints when available; use automationRole/automationAction/automationState/automationKind to discover targets and tfN only as a snapshot-local fallback. Avoid CSS selectors or translated button text.",
            "For test and math choices prefer questionId + answerOptionKey, or explicit one-based questionIndex + answerOptionIndex, rather than ambiguous visual radio labels.",
            "After UI mutations request a session snapshot with waitMs=1500..5000 instead of tight polling.",
            "If a submit click reports a transport/server error, reconcile through the authoritative GET attempt/solution API before retrying so an accepted submission is not duplicated.",
            "For code verdicts, poll Preparing/Queued/Running through the returned solution GET with bounded backoff; JudgeUnavailable means infrastructure failed for that submission, so reconcile and back off instead of tight resubmission loops.",
            "A child course listed in the catalog can still be closed by root-graph progression. COURSE_NOT_AVAILABLE means solve the visible upstream gate and refresh the root learning map; it is not COURSE_NOT_FOUND.",
            "AI accounts are ordinary users. accountType=ai grants no elevated role or hidden-data access; current resource policy removes human pacing from task solving (unlimited task energy/rate, unlimited test/math attempts, no test/math countdown, no auth cooldown after already-valid AI credentials) while invalid login guesses and Browser/network abuse stay protected."
        },
        onboarding = new
        {
            registerApi = $"{root}/api/auth/register",
            loginApi = $"{root}/api/auth/login",
            registerUi = $"{root}/register?accountType=ai",
            accountType = "ai",
            quotaStatusApi = $"{root}/api/me/quotas",
            taskPacing = new
            {
                unlimitedTaskEnergy = true,
                unlimitedTaskSubmissionRate = true,
                unlimitedTestMathAttempts = true,
                ignoreTestMathCountdowns = true,
                unlimitedValidLoginRefreshRate = true,
                invalidCredentialGuessesRemainRateLimited = true
            }
        },
        browser = new
        {
            createSession = $"{root}/api/browser/sessions",
            sessionTokenHeader = "X-TaskForge-Browser-Session-Token",
            recommendedWaitMs = new { afterNavigation = 1200, afterSubmit = 2500, max = options.MaxWaitMilliseconds },
            idleMinutes = options.SessionIdleMinutes,
            absoluteMinutes = options.SessionAbsoluteMinutes,
            semanticSnapshotVersion = "2.2",
            elementReferences = new { preferred = "automationId", fallback = "tfN", actionField = "elementId" },
            authenticatedAiRateLimitMultiplier = System.Math.Clamp(rateLimits.AuthenticatedAiMultiplier, 1, 20)
        },
        authoritativeApi = new
        {
            study = new
            {
                courses = $"{root}/api/courses",
                courseAssignments = $"{root}/api/courses/{{courseId}}/assignments",
                learningMap = $"{root}/api/courses/{{courseId}}/learning-map",
                assignment = $"{root}/api/assignments/{{assignmentId}}",
                solveShell = $"{root}/api/assignments/{{assignmentId}}/solve-shell",
                statement = $"{root}/api/assignments/{{assignmentId}}/statement",
                tests = $"{root}/api/assignments/{{assignmentId}}/tests"
            },
            code = new
            {
                submit = $"{root}/api/assignments/{{assignmentId}}/submit",
                listMine = $"{root}/api/me/solutions?assignmentId={{assignmentId}}",
                getMine = $"{root}/api/me/solutions/{{solutionId}}",
                verdictHandling = new
                {
                    pending = new[] { "Preparing", "Queued", "Running" },
                    terminal = new[] { "Accepted", "Rejected", "CompileError", "PolicyFailed", "NoTestsConfigured", "JudgeUnavailable", "LanguageNotAllowed" },
                    judgeUnavailable = "Infrastructure failure for this submission; reconcile state and use bounded backoff before a new submission."
                }
            },
            test = new
            {
                start = $"{root}/api/task-tests/{{assignmentId}}/start",
                submit = $"{root}/api/task-tests/{{assignmentId}}/submit",
                getAttempt = $"{root}/api/me/test-attempts/{{attemptId}}"
            },
            math = new
            {
                start = $"{root}/api/math-tasks/{{assignmentId}}/start",
                submit = $"{root}/api/math-tasks/{{assignmentId}}/submit",
                getAttempt = $"{root}/api/me/math-attempts/{{attemptId}}"
            },
            recoveryRule = "After an uncertain UI/HTTP submit, query the matching GET endpoint first. Retry the mutation only when the server has not recorded the submission."
        },
        workflow = new object[]
        {
            new { step = 1, action = "register-login", note = "POST /api/auth/register with accountType=ai, then POST /api/auth/login and keep the returned ordinary access token." },
            new { step = 2, action = "create-session", note = "Create readOnly=false session with Authorization: Bearer <access-token>, site=main, path=/courses. GET-only clients can use /.well-known/taskforge-ai-browser.json and /api/ai/browser/start instead." },
            new { step = 3, action = "choose-course", targetRole = "course-card", preferredState = "incomplete" },
            new { step = 4, action = "choose-assignment", targetRoles = new[] { "course-map-node", "assignment-card" }, targetAction = "open-assignment", preferredState = "unsolved", note = "Flow-map and card modes expose the same stable assignment-{id} automationId. In Browser Automation, one click on a flow assignment node performs open-assignment; normal human map behavior remains unchanged." },
            new { step = 5, action = "inspect-assignment", note = "Read the semantic snapshot and assignment kind. If ordinary HTTP is available, GET the assignment/solve-shell/statement/tests directly. For test/math choices bind answers to questionId + answerOptionKey whenever available." },
            new { step = 6, action = "submit", note = "Prefer the authoritative ordinary API for reliable serial solving. If using the UI, submit with waitMs=2500..5000 and includeSnapshot=true." },
            new { step = 7, action = "verify-reconcile", note = "For code query /api/me/solutions; for tests/math query the attempt by id. Treat those APIs as authoritative even if the UI lost the submit response." },
            new { step = 8, action = "continue", targetAction = "next-assignment" }
        }
    });
})
.WithName("GetAgentPlaybook")
.WithTags("Discovery")
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
    AgentAccessService access,
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
    if (!caller.IsAuthenticated)
    {
        access.EnrichDiscoveredLinks(result.Snapshot, result.Snapshot.Site);
    }
    ApplyArtifactHeaders(http.Response, result.CacheHit, false, false, false, result.Snapshot.Viewport.Width, result.Snapshot.Viewport.Height, options, caller);
    return Results.Ok(result.Snapshot);
})
.WithName("GetSiteSnapshot")
.WithTags("Site inspection")
.Produces<SiteSnapshotResponse>(StatusCodes.Status200OK)
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

app.MapGet("/api/site/agent/capture/{site}/{width:int}/{height:int}/{mode}/{**path}", async (
    string site,
    int width,
    int height,
    string mode,
    string? path,
    HttpContext http,
    PublicAgentCaptureService captures,
    AgentAccessService access,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    if (caller.IsAuthenticated)
    {
        throw new BrowserApiException(
            StatusCodes.Status400BadRequest,
            "PUBLIC_ARTIFACT_REQUIRES_ANONYMOUS",
            "Crawler-артефакты публикуются только для анонимного read-only просмотра. Для приватных страниц используйте обычный Browser API с Authorization header.");
    }

    await EnforceRateLimit(http, limiter, caller, "render", limits.RenderLimit, limits.RenderWindowSeconds, ct);
    var fullPage = mode.ToLowerInvariant() switch
    {
        "full" => true,
        "viewport" => false,
        _ => throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_CAPTURE_MODE", "Режим capture должен быть full или viewport.")
    };
    var relativePath = string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.TrimStart('/');
    var capture = await captures.CaptureAsync(site, relativePath, width, height, fullPage, ct);
    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers["X-Robots-Tag"] = "noindex, noarchive, nosnippet";
    http.Response.Headers["X-TaskForge-Agent-Artifact-Id"] = capture.Manifest.Id;
    http.Response.Headers["X-TaskForge-Capture-Cache"] = capture.CacheHit ? "HIT" : "MISS";
    http.Response.Headers["X-TaskForge-Snapshot-Version"] = capture.Snapshot.SemanticSnapshotVersion;
    return Results.Content(access.BuildCaptureHtml(http.Request, capture.Manifest, capture.Snapshot), "text/html; charset=utf-8");
})
.WithName("CreatePublicAgentCapture")
.WithTags("Agent access")
.AllowAnonymous();

app.MapGet("/ai-artifacts/{id}/{fileName}", async (
    string id,
    string fileName,
    HttpContext http,
    PublicAgentArtifactStore artifacts,
    BrowserRateLimitOptions limits,
    BrowserCallerResolver callerResolver,
    RedisFixedWindowRateLimiter limiter,
    CancellationToken ct) =>
{
    var caller = callerResolver.Resolve(http);
    callerResolver.ThrowIfInvalidCredential(caller);
    await EnforceRateLimit(http, limiter, caller, "metadata", limits.MetadataLimit, limits.MetadataWindowSeconds, ct);

    http.Response.Headers["X-Robots-Tag"] = "noindex, noarchive, nosnippet";
    var bundle = await artifacts.GetAsync(id, ct);
    if (bundle is null)
    {
        if (PublicAgentArtifactStore.IsValidId(id))
        {
            throw new BrowserApiException(StatusCodes.Status410Gone, "AGENT_ARTIFACT_EXPIRED_OR_MISSING", "Временный Browser API артефакт уже недоступен. Создайте новый capture вместо повторного использования старой artifact-ссылки.");
        }

        throw new BrowserApiException(StatusCodes.Status404NotFound, "AGENT_ARTIFACT_NOT_FOUND", "Публичный Browser API артефакт не найден.");
    }

    var maxAge = Math.Max(0, (int)Math.Floor((bundle.Manifest.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
    http.Response.Headers.CacheControl = $"public, max-age={maxAge}, immutable";
    http.Response.Headers["X-TaskForge-Agent-Artifact-Id"] = bundle.Manifest.Id;
    http.Response.Headers["X-TaskForge-Agent-Artifact-Expires"] = bundle.Manifest.ExpiresAtUtc.ToString("O");
    http.Response.Headers["X-TaskForge-Render-Width"] = bundle.Manifest.Width.ToString();
    http.Response.Headers["X-TaskForge-Render-Height"] = bundle.Manifest.Height.ToString();

    return fileName.ToLowerInvariant() switch
    {
        "snapshot.json" => Results.File(bundle.SnapshotJson, "application/json; charset=utf-8", enableRangeProcessing: false),
        "render.png" => Results.File(bundle.Png, "image/png", enableRangeProcessing: true),
        "render.pdf" when bundle.Pdf is not null => Results.File(bundle.Pdf, "application/pdf", enableRangeProcessing: true),
        "render.pdf" => throw new BrowserApiException(StatusCodes.Status404NotFound, "AGENT_ARTIFACT_PDF_UNAVAILABLE", "PDF-обёртка для этого артефакта недоступна. Используйте authoritative Chromium PNG."),
        _ => throw new BrowserApiException(StatusCodes.Status404NotFound, "AGENT_ARTIFACT_FILE_NOT_FOUND", "Неизвестная часть Browser API артефакта.")
    };
})
.WithName("GetPublicAgentArtifact")
.WithTags("Agent access")
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
        new BrowserSessionHeader("X-TaskForge-Browser-Session-Token", created.RawToken),
        created.Snapshot));
})
.WithName("CreateBrowserSession")
.WithTags("Interactive browser")
.Produces<CreateBrowserSessionResponse>(StatusCodes.Status201Created)
.AllowAnonymous();

app.MapGet("/api/browser/sessions/{id:guid}/snapshot", async (
    Guid id,
    HttpContext http,
    bool? includeText,
    int? waitMs,
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
    http.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await sessions.SnapshotAsync(
        id,
        SessionToken(http),
        caller,
        includeText ?? true,
        System.Math.Clamp(waitMs ?? 0, 0, options.MaxWaitMilliseconds),
        ct));
})
.WithName("GetBrowserSessionSnapshot")
.WithTags("Interactive browser")
.Produces<SiteSnapshotResponse>(StatusCodes.Status200OK)
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
    .WithName("NavigateBrowserSession").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/click", async (Guid id, HttpContext http, ClickBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.ClickAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("ClickBrowserElement").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/fill", async (Guid id, HttpContext http, FillBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.FillAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("FillBrowserElement").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/press", async (Guid id, HttpContext http, PressBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.PressAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("PressBrowserElementKey").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/select", async (Guid id, HttpContext http, SelectBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.SelectAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("SelectBrowserElementOption").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/hover", async (Guid id, HttpContext http, HoverBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.HoverAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("HoverBrowserElement").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/check", async (Guid id, HttpContext http, CheckBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.CheckAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("SetBrowserElementChecked").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/scroll", async (Guid id, HttpContext http, ScrollBrowserSessionRequest request, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.ScrollAsync(id, SessionToken(http), caller, request, ct)))
    .WithName("ScrollBrowserSession").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/back", async (Guid id, HttpContext http, bool? includeSnapshot, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.BackAsync(id, SessionToken(http), caller, includeSnapshot ?? true, ct)))
    .WithName("GoBackBrowserSession").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

app.MapPost("/api/browser/sessions/{id:guid}/reload", async (Guid id, HttpContext http, bool? includeSnapshot, BrowserSessionRegistry sessions, BrowserRateLimitOptions limits, BrowserCallerResolver callers, RedisFixedWindowRateLimiter limiter, CancellationToken ct) =>
    await SessionAction(http, callers, limiter, limits, ct, caller => sessions.ReloadAsync(id, SessionToken(http), caller, includeSnapshot ?? true, ct)))
    .WithName("ReloadBrowserSession").WithTags("Interactive browser").Produces<BrowserActionResponse>(StatusCodes.Status200OK).AllowAnonymous();

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
.Produces(StatusCodes.Status204NoContent)
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
    var rateOptions = http.RequestServices.GetRequiredService<BrowserRateLimitOptions>();
    var aiMultiplier = caller.IsAuthenticated && string.Equals(caller.AccountType, "ai", StringComparison.OrdinalIgnoreCase)
        ? System.Math.Clamp(rateOptions.AuthenticatedAiMultiplier, 1, 20)
        : 1;
    var effectiveLimit = System.Math.Min(1_000_000, System.Math.Max(1, limit) * aiMultiplier);
    if (aiMultiplier > 1)
    {
        http.Response.Headers["X-TaskForge-AI-Rate-Multiplier"] = aiMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    var decision = await limiter.CheckAsync(bucket, caller.OwnerKey, caller.NetworkKey, effectiveLimit, windowSeconds, cancellationToken);
    var resetAfterSeconds = Math.Max(0, (int)Math.Ceiling((decision.ResetAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
    var resetUnix = decision.ResetAtUtc.ToUnixTimeSeconds().ToString();
    http.Response.Headers["RateLimit-Limit"] = decision.Limit.ToString();
    http.Response.Headers["RateLimit-Remaining"] = decision.Remaining.ToString();
    http.Response.Headers["RateLimit-Reset"] = resetAfterSeconds.ToString();
    http.Response.Headers["RateLimit-Policy"] = $"{decision.Limit};w={decision.WindowSeconds}";
    http.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString();
    http.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString();
    http.Response.Headers["X-RateLimit-Reset"] = resetUnix;
    if (!decision.Allowed)
    {
        http.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
        throw new BrowserApiException(
            StatusCodes.Status429TooManyRequests,
            "RATE_LIMITED",
            "Слишком много запросов к Browser API. Подождите и повторите попытку.",
            decision.RetryAfterSeconds,
            new
            {
                decision.Bucket,
                decision.Limit,
                decision.Remaining,
                decision.WindowSeconds,
                decision.RetryAfterSeconds,
                decision.ResetAtUtc
            });
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
    EnsureRange(options.AppReadyTimeoutMilliseconds, 250, 15000, "Browser:AppReadyTimeoutMilliseconds");
    EnsureRange(options.FontReadyTimeoutMilliseconds, 100, 10000, "Browser:FontReadyTimeoutMilliseconds");
    EnsureRange(options.CaptureTimeoutSeconds, 10, 180, "Browser:CaptureTimeoutSeconds");
    EnsureRange(options.CaptureCacheSeconds, 0, 3600, "Browser:CaptureCacheSeconds");
    EnsureRange(options.RecommendedCaptureConcurrency, 1, 16, "Browser:RecommendedCaptureConcurrency");
    EnsureRange(options.MaxConcurrentPublicCaptures, 1, 16, "Browser:MaxConcurrentPublicCaptures");
    EnsureRange(options.PublicCaptureQueueWaitMilliseconds, 0, 5000, "Browser:PublicCaptureQueueWaitMilliseconds");
    if (options.DefaultWaitMilliseconds > options.MaxWaitMilliseconds)
        throw new InvalidOperationException("Browser:DefaultWaitMilliseconds cannot exceed Browser:MaxWaitMilliseconds.");

    EnsureRange(options.MinViewportWidth, 240, 4096, "Browser:MinViewportWidth");
    EnsureRange(options.MaxViewportWidth, options.MinViewportWidth, 4096, "Browser:MaxViewportWidth");
    EnsureRange(options.MinViewportHeight, 240, 4096, "Browser:MinViewportHeight");
    EnsureRange(options.MaxViewportHeight, options.MinViewportHeight, 4096, "Browser:MaxViewportHeight");
    EnsureRange(options.MaxFullPageHeight, options.MaxViewportHeight, 50000, "Browser:MaxFullPageHeight");
    EnsureLongRange(options.MaxScreenshotPixels, 1_000_000L, 100_000_000L, "Browser:MaxScreenshotPixels");
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
    EnsureRange(options.AgentArtifactTtlSeconds, 60, 86400, "Browser:AgentArtifactTtlSeconds");
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
    EnsureRange(rateOptions.AuthenticatedAiMultiplier, 1, 20, "BrowserRateLimits:AuthenticatedAiMultiplier");
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

static void ValidateAiRemoteConfiguration(AiRemoteBrowserOptions options, BrowserOptions browser)
{
    EnsureRange(options.StartChallengeTtlSeconds, 30, 1800, "AiRemoteBrowser:StartChallengeTtlSeconds");
    EnsureRange(options.SessionIdleMinutes, 5, 120, "AiRemoteBrowser:SessionIdleMinutes");
    EnsureRange(options.SessionAbsoluteMinutes, options.SessionIdleMinutes, 240, "AiRemoteBrowser:SessionAbsoluteMinutes");
    EnsureRange(options.MaxSessionsPerNetwork, 1, 10, "AiRemoteBrowser:MaxSessionsPerNetwork");
    EnsureRange(options.MaxValueCharacters, 1000, 20000, "AiRemoteBrowser:MaxValueCharacters");
    EnsureRange(options.DefaultWidth, browser.MinViewportWidth, browser.MaxViewportWidth, "AiRemoteBrowser:DefaultWidth");
    EnsureRange(options.DefaultHeight, browser.MinViewportHeight, browser.MaxViewportHeight, "AiRemoteBrowser:DefaultHeight");
    EnsureRange(options.DefaultWaitMilliseconds, 0, browser.MaxWaitMilliseconds, "AiRemoteBrowser:DefaultWaitMilliseconds");
    EnsureRateRule(options.StartLimit, options.StartWindowSeconds, "AiRemoteBrowser:Start");
    EnsureRateRule(options.ConfirmLimit, options.ConfirmWindowSeconds, "AiRemoteBrowser:Confirm");
    EnsureRateRule(options.ActionLimit, options.ActionWindowSeconds, "AiRemoteBrowser:Action");
    EnsureRateRule(options.ScreenshotLimit, options.ScreenshotWindowSeconds, "AiRemoteBrowser:Screenshot");
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

static void EnsureLongRange(long value, long min, long max, string name)
{
    if (value < min || value > max)
        throw new InvalidOperationException($"{name} must be in range {min}..{max}.");
}
