using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed class DiscoveryDocumentService(BrowserUrlPolicy urlPolicy, BrowserOptions options)
{
    private readonly BrowserUrlPolicy _urlPolicy = urlPolicy;
    private readonly BrowserOptions _options = options;

    public object BuildDiscovery(HttpRequest request)
    {
        var root = PublicRoot(request);
        return new
        {
            name = "TaskForge.by",
            purpose = "Educational platform for programming courses, study notes, assignments, automated checking and progress tracking, with machine-readable inspection and controlled Chromium APIs.",
            agentAccess = $"{root}/ai-access",
            website = _urlPolicy.Sites.ToDictionary(x => x.Key, x => x.Value.AbsoluteUri.TrimEnd('/')),
            anonymousBrowsing = new
            {
                supported = true,
                readOnly = true,
                note = "Anonymous callers see the same public pages as unauthenticated human visitors. Anonymous interactive sessions cannot enable mutations."
            },
            aiAccounts = new
            {
                supported = true,
                registrationPage = $"{root}/register?accountType=ai",
                registrationApi = $"{root}/api/auth/register",
                loginApi = $"{root}/api/auth/login",
                accountTypeField = "accountType",
                value = "ai",
                note = "AI accounts are ordinary TaskForge.by users. The marker is self-declared and grants no extra role or permission."
            },
            siteInspection = new
            {
                info = $"{root}/api/site/info",
                routes = $"{root}/api/site/routes",
                snapshot = $"{root}/api/site/snapshot?path=/courses&width=390&height=844",
                renderPng = $"{root}/api/site/render?path=/courses&width=390&height=844",
                renderAnnotatedPng = $"{root}/api/site/render?path=/courses&width=390&height=844&annotated=true",
                renderPdf = $"{root}/api/site/render.pdf?path=/courses&width=390&height=844",
                crawlerCapture = $"{root}/api/site/agent/capture/main/390/844/viewport/",
                recommendedCaptureConcurrency = _options.RecommendedCaptureConcurrency,
                captureTimeoutSeconds = _options.CaptureTimeoutSeconds,
                captureCacheSeconds = _options.CaptureCacheSeconds,
                captureSettleMilliseconds = _options.AgentCaptureWaitMilliseconds,
                artifactTtlSeconds = _options.AgentArtifactTtlSeconds,
                notes = new[]
                {
                    "Use site=ct for the CT frontend.",
                    "Snapshot elements receive stable-within-snapshot tfN references for interactive session actions.",
                    "Crawler capture snapshots include source, current-viewport, mobile and desktop follow-up capture links; mobile/desktop discovery links prefer viewport mode for low latency.",
                    "Do not issue parallel expensive captures beyond recommendedCaptureConcurrency. Identical public captures are single-flight coalesced and briefly cached.",
                    "Public crawler captures synchronously persist snapshot JSON and PNG only. Use /api/site/render.pdf explicitly when a PDF is required."
                }
            },
            semanticSnapshot = new
            {
                version = "2.0",
                topLevelReadiness = new[] { "captureMode", "policyInterference", "pageReadyState", "appReady", "readiness" },
                diagnostics = new[] { "console", "networkFailures", "httpErrors", "policyBlockedRequests" },
                visibility = "Interactive elements respect hidden, aria-hidden, inert, closed details, CSS visibility and clipping ancestors.",
                policyBlockedRequests = "Requests intentionally blocked by anonymous/read-only inspector policy are expected diagnostics and are not counted as networkFailures."
            },
            interactiveBrowser = new
            {
                sessions = $"{root}/api/browser/sessions",
                sessionTokenHeader = "X-TaskForge-Browser-Session-Token",
                defaultReadOnly = true,
                mutatingModeRequiresTaskForgeAccessToken = true,
                anonymousSessionAuthorization = "session-token",
                authenticatedSessionAuthorization = "same-taskforge-user+session-token",
                actions = new[] { "navigate", "snapshot", "screenshot", "click", "fill", "press", "select", "hover", "check", "scroll", "back", "reload", "close" }
            },
            rateLimits = new
            {
                headers = new[] { "RateLimit-Limit", "RateLimit-Remaining", "RateLimit-Reset", "RateLimit-Policy" },
                compatibilityHeaders = new[] { "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset" },
                resetSemantics = "RateLimit-Reset is seconds until reset; X-RateLimit-Reset is the UTC Unix timestamp.",
                rejectedRequestHeaders = new[] { "Retry-After" },
                status = 429
            },
            openApi = $"{root}/api/browser/openapi.json",
            instructions = $"{root}/llms.txt",
            apiVersion = "1.2",
            deploymentVersion = _options.DeploymentVersion,
            safety = new
            {
                relativePathsOnly = true,
                arbitraryUrls = false,
                rateLimited = true,
                sessionTtl = true,
                browserOrigins = _urlPolicy.Sites.Keys.ToArray()
            }
        };
    }

    public string BuildLlmsText(HttpRequest request)
    {
        var root = PublicRoot(request);
        return $$"""
# TaskForge.by automated exploration

TaskForge.by is an educational platform for programming courses, study notes, coding and test assignments, automated solution checking, progress tracking and ratings. It openly supports automated study of its public website through ordinary browser access, semantic snapshots, visual Chromium renders and controlled interactive browser sessions.

## Sites
- Main: {{_urlPolicy.Sites.GetValueOrDefault("main")?.AbsoluteUri.TrimEnd('/')}}
- CT: {{_urlPolicy.Sites.GetValueOrDefault("ct")?.AbsoluteUri.TrimEnd('/')}}
- Add `site=ct` to inspection/session requests for the CT frontend. The default site is `main`.

## Start from only the domain name
If an automated client knows only `{{root}}` and cannot execute the React application, open `{{root}}/ai-access`. The root HTML advertises this crawler entry point, `/.well-known/taskforge-ai.json` and `/llms.txt` without requiring JavaScript.

`/ai-access` contains ordinary crawlable links to low-latency viewport captures. A public capture generates short-lived immutable URLs for snapshot JSON and the authoritative Chromium PNG. Use `/api/site/render.pdf` explicitly when a PDF is required. Capture pages and snapshot JSON also expose follow-up links for the source page, the current viewport, mobile 390x844 and desktop 1440x900.

## Anonymous visitors
Anonymous agents see the same public pages as unauthenticated human visitors. They do not need a special URL or share token. Anonymous interactive sessions are always read-only.

## AI accounts
Agents may create an ordinary TaskForge.by account with `POST {{root}}/api/auth/register` and `accountType: "ai"`.
The registration UI is `{{root}}/register?accountType=ai`.
The AI marker is self-declared. It does not grant an admin role, hidden endpoint or additional permission.
Reuse one account per agent or integration instead of creating disposable accounts.

Example registration body:
```json
{
  "login": "my-agent-name",
  "password": "use-a-strong-unique-password",
  "firstName": "My agent",
  "lastName": "",
  "accountType": "ai"
}
```

Log in through `POST {{root}}/api/auth/login`. Authenticated Browser API calls accept the ordinary TaskForge access token in `Authorization: Bearer <token>`.

## Stateless site inspection
- Capabilities: `GET {{root}}/api/site/info`
- Known routes: `GET {{root}}/api/site/routes`
- Routes for one frontend: `GET {{root}}/api/site/routes?site=main`
- Semantic snapshot: `GET {{root}}/api/site/snapshot?path=/courses&width=390&height=844`
- PNG: `GET {{root}}/api/site/render?path=/courses&width=390&height=844`
- Annotated PNG: `GET {{root}}/api/site/render?path=/courses&width=390&height=844&annotated=true`
- PDF: `GET {{root}}/api/site/render.pdf?path=/courses&width=390&height=844`
- Crawler-friendly fast capture page: `GET {{root}}/api/site/agent/capture/main/390/844/viewport/courses`
- Full-page crawler capture when explicitly needed: `GET {{root}}/api/site/agent/capture/main/390/844/full/courses`

Semantic snapshot version `2.0` contains visible text, headings, document/viewport dimensions, horizontal overflow, truly visible interactive controls, raw and clipped bounds, accessibility/layout issues, console diagnostics, real network failures, expected inspector-policy blocks and performance measurements.

Important top-level fields:
- `captureMode`: anonymous-read-only, authenticated-read-only or authenticated-interactive.
- `policyInterference`: true when the inspector intentionally blocked one or more requests.
- `pageReadyState`, `appReady` and `readiness`: bounded DOM/app/font stabilization diagnostics, pending request count and safe pending paths.
- `policyBlockedRequests`: expected blocks such as read-only POST, WebSocket or non-allowlisted origin. Do not treat these as site failures.
- `networkFailures`: failures not explained by inspector policy.
- `discoveredLinks`: source path plus ready-made current/mobile/desktop crawler capture URLs.

Interactive controls are assigned `tf1`, `tf2`, ... references. These references are valid for the current page state and should be refreshed after navigation or major DOM changes. Hidden, inert, aria-hidden, clipped and closed-collapsible controls are excluded. The `ariaSnapshot` field uses Playwright's AI-oriented ARIA representation with bounding boxes when available.

## Interactive Chromium sessions
Create a session:
```http
POST {{root}}/api/browser/sessions
Content-Type: application/json

{
  "site": "main",
  "path": "/courses",
  "width": 390,
  "height": 844,
  "readOnly": true
}
```

The response returns an `id` and a random session token. Send that token on every session request:
```http
X-TaskForge-Browser-Session-Token: <session token>
```

Anonymous read-only sessions use X-TaskForge-Browser-Session-Token as their session credential and are not bound to a source IP. Authenticated sessions additionally require the same TaskForge.by access-token identity that created them.

Available actions are navigate, snapshot, screenshot, click, fill, press, select, hover, check, scroll, back, reload and close. Use `elementId` values from the latest snapshot rather than CSS selectors.

Read-only sessions block every non-safe same-origin HTTP request. `readOnly: false` is accepted only for authenticated TaskForge.by users and still grants no permissions beyond that user's ordinary account rights.

The PNG is the pixel-authoritative visual render. The PDF endpoint is only a compatibility wrapper for clients that can inspect PDFs but cannot fetch images.

## API schema
- OpenAPI: `{{root}}/api/browser/openapi.json`
- Discovery: `{{root}}/.well-known/taskforge-ai.json`
- The OpenAPI schema documents the `X-TaskForge-Browser-Session-Token` API-key security scheme, request/response schemas, examples, image/PDF media types, login/registration helpers, and 400/401/403/404/409/410/413/423/429/502/503/504 responses.

## Rate limits and capture discipline
- Recommended expensive-capture concurrency: {{_options.RecommendedCaptureConcurrency}}.
- Public capture timeout: {{_options.CaptureTimeoutSeconds}} seconds.
- Identical public captures are coalesced and cached for {{_options.CaptureCacheSeconds}} seconds; public immutable artifacts live for {{_options.AgentArtifactTtlSeconds}} seconds.
- Read `RateLimit-Limit`, `RateLimit-Remaining`, `RateLimit-Reset` and `RateLimit-Policy` on responses. `RateLimit-Reset` is seconds until reset; compatibility `X-RateLimit-Reset` is a UTC Unix timestamp. A 429 also includes `Retry-After`.
- Cache public stateless renders when practical and never flood parallel render/capture requests.

## Safety
- Only configured TaskForge.by origins and explicitly configured static-resource origins can be requested.
- Inspection accepts relative TaskForge paths, never an arbitrary URL.
- Browser API endpoints cannot recursively render themselves.
- Calls are protected by Nginx limits, Redis-backed endpoint quotas, global Chromium capacity limits and short session TTLs.
- Third-party resources can be intentionally blocked unless the operator allowlists their origin; expected policy blocks are reported separately from real failures.
""";
    }

    private static string PublicRoot(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
        return $"{scheme}://{host}".TrimEnd('/');
    }
}
