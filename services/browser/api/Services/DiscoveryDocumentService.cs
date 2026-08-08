using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed class DiscoveryDocumentService(BrowserOptions options, BrowserUrlPolicy urlPolicy)
{
    private readonly BrowserOptions _options = options;
    private readonly BrowserUrlPolicy _urlPolicy = urlPolicy;

    public object BuildDiscovery(HttpRequest request)
    {
        var root = PublicRoot(request);
        return new
        {
            name = "TaskForge",
            purpose = "Open learning platform with machine-readable inspection and real Chromium browser APIs.",
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
                note = "AI accounts are ordinary TaskForge users. The marker is self-declared and grants no extra role or permission."
            },
            siteInspection = new
            {
                info = $"{root}/api/site/info",
                routes = $"{root}/api/site/routes",
                snapshot = $"{root}/api/site/snapshot?path=/courses&width=390&height=844",
                renderPng = $"{root}/api/site/render?path=/courses&width=390&height=844",
                renderAnnotatedPng = $"{root}/api/site/render?path=/courses&width=390&height=844&annotated=true",
                renderPdf = $"{root}/api/site/render.pdf?path=/courses&width=390&height=844",
                notes = new[]
                {
                    "Use site=ct for the CT frontend.",
                    "Snapshot elements receive stable-within-snapshot tfN references for interactive session actions.",
                    "The ARIA snapshot uses Playwright AI mode and includes element boxes when supported."
                }
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
            openApi = $"{root}/api/browser/openapi.json",
            instructions = $"{root}/llms.txt",
            apiVersion = "1.1",
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
# TaskForge automated exploration

TaskForge openly supports automated study of its public website through ordinary browser access, semantic snapshots, visual Chromium renders and controlled interactive browser sessions.

## Sites
- Main: {{_urlPolicy.Sites.GetValueOrDefault("main")?.AbsoluteUri.TrimEnd('/')}}
- CT: {{_urlPolicy.Sites.GetValueOrDefault("ct")?.AbsoluteUri.TrimEnd('/')}}
- Add `site=ct` to inspection/session requests for the CT frontend. The default site is `main`.

## Anonymous visitors
Anonymous agents see the same public pages as unauthenticated human visitors. They do not need a special URL or share token.
Anonymous interactive sessions are always read-only.

## AI accounts
Agents may create an ordinary TaskForge account with `POST {{root}}/api/auth/register` and `accountType: "ai"`.
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

A snapshot contains visible text, headings, document/viewport dimensions, horizontal overflow, interactive controls, bounds, accessibility/layout issues, console errors, failed requests and performance measurements.
Interactive controls are assigned `tf1`, `tf2`, ... references. These references are valid for the current page state and should be refreshed after navigation or major DOM changes.
The `ariaSnapshot` field uses Playwright's AI-oriented ARIA representation with bounding boxes when available.

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

The response returns an `id` and a random session token. Send that token on every session request. Anonymous read-only sessions use the session token as their bearer credential and are not bound to a source IP, so a proxy/CDN route change does not break the session. Authenticated sessions additionally require the same TaskForge user access token identity that created them:
```http
X-TaskForge-Browser-Session-Token: <session token>
```

Available actions are navigate, snapshot, screenshot, click, fill, press, select, hover, check, scroll, back, reload and close. Use `elementId` values from the latest snapshot rather than CSS selectors.

Read-only sessions block every non-safe same-origin HTTP request. `readOnly: false` is accepted only for authenticated TaskForge users and still grants no permissions beyond that user's ordinary account rights.

## API schema
- OpenAPI: `{{root}}/api/browser/openapi.json`
- Discovery: `{{root}}/.well-known/taskforge-ai.json`

## Safety and limits
- Only configured TaskForge origins and explicitly configured static-resource origins can be requested.
- Inspection accepts relative TaskForge paths, never an arbitrary URL.
- Browser API endpoints cannot recursively render themselves.
- Calls are protected by Nginx limits, Redis-backed endpoint quotas, global Chromium capacity limits and short session TTLs.
- Cache public stateless renders when practical and avoid parallel render floods.
- Some third-party images may be intentionally blocked unless the operator explicitly allowlists their origin; failed requests are reported in snapshots.
""";
    }

    private static string PublicRoot(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
        return $"{scheme}://{host}".TrimEnd('/');
    }
}
