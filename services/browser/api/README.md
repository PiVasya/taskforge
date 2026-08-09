# TaskForge Browser API

`browser-api` runs a real Chromium process and exposes TaskForge-only inspection endpoints for AI agents, external bots, UI audits and automated smoke tests. It renders the already deployed React applications; it does not rebuild the frontend and it is not a general-purpose web proxy.

## Discovery

```text
GET /.well-known/taskforge-ai.json
GET /llms.txt
GET /api/browser/openapi.json
GET /api/site/info
GET /api/site/routes
GET /api/site/routes?site=ct
GET /api/site/agent/playbook
```

The discovery document tells an agent how to register an ordinary account with `accountType: "ai"`, how to inspect public pages and how to create an interactive browser session.


## Domain-only self-discovery

Agents that know only `https://taskforge.by` can discover the Browser API without the user pasting a render URL. The frontend root HTML advertises `/ai-access`, `/.well-known/taskforge-ai.json` and `/llms.txt` in its no-JavaScript fallback.

`GET /ai-access` is a plain crawlable HTML index. For anonymous public routes it exposes queryless capture links such as:

```text
GET /api/site/agent/capture/main/390/844/viewport/
GET /api/site/agent/capture/main/1440/900/viewport/news
```

A capture opens the real page in Chromium once and persists short-lived Redis artifacts under content-addressed SHA-256 ids:

```text
GET /ai-artifacts/{id}/snapshot.json
GET /ai-artifacts/{id}/render.png
GET /ai-artifacts/{id}/render.pdf
```

The PNG is pixel-authoritative. The PDF is a compatibility wrapper for clients that can visually inspect PDFs but cannot fetch dynamic images. Public artifact creation rejects explicitly authenticated callers so private page content cannot be exposed through a public artifact URL.

The capture HTML also lists safe same-origin links discovered in the rendered page as new capture links, so a GET-only crawler can continue navigating public TaskForge pages without constructing Browser API query strings itself.

## Stateless inspection

```text
GET /api/site/snapshot?path=/courses&width=390&height=844
GET /api/site/render?path=/courses&width=390&height=844&fullPage=true
GET /api/site/render?path=/courses&width=390&height=844&annotated=true
GET /api/site/render.pdf?path=/courses&width=1440&height=900&fullPage=true
```

Use `site=ct` for the CT frontend. Add an ordinary TaskForge access token as `Authorization: Bearer ...` to render pages available to that user. Browser API authentication is intentionally explicit: ambient `tf_at` cookies are ignored. Stateless renders are always read-only.

The semantic snapshot contract version is `2.1`. A semantic snapshot includes:

- `semanticSnapshotVersion`, capture mode, current `document.readyState`, the TaskForge app-ready marker and the stage where stabilization finished;
- visible page text and headings;
- Playwright AI-mode ARIA snapshot with element boxes when supported;
- viewport and document dimensions;
- horizontal overflow and offending elements;
- interactive controls with `tf1`, `tf2`, ... references, clipped visible bounds and visible-area ratios;
- touch-target, label, heading, image-alt and duplicate-ID diagnostics;
- console errors, real failed requests and HTTP errors;
- requests intentionally blocked by the read-only/origin policy in a separate `policyBlockedRequests` collection;
- bounded pending-request diagnostics when a page does not become ready;
- navigation, FCP/LCP/CLS, resource and long-task measurements.

`annotated=true` overlays the same `tfN` references on the PNG.

## AI accounts

An AI account is an ordinary `IdentityUser` with:

```json
{
  "accountType": "ai"
}
```

`AccountType` is separate from `Role`. It is a self-declared marker for UI labels, administration and analytics; it grants no additional authorization.

Anonymous agents may inspect public pages. To access authenticated pages, an agent registers through the normal `/api/auth/register` endpoint and logs in through `/api/auth/login`.

## Interactive sessions

Create a session:

```http
POST /api/browser/sessions
Content-Type: application/json

{
  "site": "main",
  "path": "/courses",
  "width": 390,
  "height": 844,
  "readOnly": true,
  "waitMs": 500
}
```

The response contains a random high-entropy session token. Send it on every subsequent session request. Anonymous sessions are authorized by this session token rather than by a source IP, so they continue to work through CDNs, NAT rebinding and network changes. Authenticated sessions additionally require the same TaskForge user identity that created the session:

```http
X-TaskForge-Browser-Session-Token: <token>
```

Available endpoints:

```text
GET    /api/browser/sessions/{id}/snapshot
GET    /api/browser/sessions/{id}/screenshot
POST   /api/browser/sessions/{id}/navigate
POST   /api/browser/sessions/{id}/click
POST   /api/browser/sessions/{id}/fill
POST   /api/browser/sessions/{id}/press
POST   /api/browser/sessions/{id}/select
POST   /api/browser/sessions/{id}/hover
POST   /api/browser/sessions/{id}/check
POST   /api/browser/sessions/{id}/scroll
POST   /api/browser/sessions/{id}/back
POST   /api/browser/sessions/{id}/reload
DELETE /api/browser/sessions/{id}
```

Actions use `elementId` values from the latest snapshot, not arbitrary CSS selectors. Obtain a new snapshot after navigation or a major DOM change because references are intentionally scoped to the current page state. Action bodies also accept optional `waitMs` up to 15000 ms; use it when a click/submit triggers asynchronous work and you want the returned action snapshot only after a deliberate stabilization window.

Anonymous sessions are always read-only. `readOnly=false` requires a valid ordinary TaskForge access token and still grants only that account's existing permissions.

The generated OpenAPI document formally describes the session-token header as the `BrowserSessionToken` API-key security scheme. An authenticated session requires both that header and the same ordinary TaskForge bearer identity that created it. The document also contains registration/login helper schemas, binary PNG/PDF media types and structured error/rate-limit responses.

The .NET 10 Minimal API validation pipeline is enabled for request records. Invalid `elementId`, viewport, key length and other annotated fields are rejected before Chromium work starts; OpenAPI documents both TaskForge `ApiError` and framework validation-problem responses.

## Security model

- Input contains a configured site key plus a relative TaskForge path. Request contracts expose no arbitrary URL field.
- Chromium document navigation cannot leave the selected TaskForge origin.
- Subresources are limited to configured TaskForge origins and explicit `Browser:AllowedExternalOrigins` entries.
- `/api/site` and `/api/browser` cannot recursively render themselves.
- Read-only contexts abort every non-safe same-origin HTTP request, not only `/api/*` calls.
- Popups are closed; service workers and downloads are disabled.
- Nginx applies an inexpensive request/connection shield before the request reaches the service.
- Application quotas use atomic Redis `INCR` + `EXPIRE` Lua scripts, with a process-local emergency fallback.
- Identical public capture jobs use process-local single-flight to coalesce concurrent work inside one replica, plus a short Redis pointer cache that prevents repeat work after a capture is published across replicas.
- Chromium operations, active sessions, per-owner sessions, viewport, full-page pixels, wait time, response bytes and session lifetime are bounded.
- Session tokens are stored only as SHA-256 hashes and compared in constant time. Anonymous read-only sessions use the token as their session credential; authenticated sessions require both the token and the same TaskForge user identity.
- Console and network diagnostics remove query strings and redact common token/password forms.
- Authenticated snapshots and renders are never shared through the public artifact cache.
- The container runs as `pwuser`, has a read-only root filesystem, drops all Linux capabilities, has no Docker socket and receives CPU/RAM/PID/temp/shm limits.

Third-party images are intentionally blocked unless their origin is explicitly allowlisted. Expected inspector-policy blocks are reported in `policyBlockedRequests`, separately from genuine console and network failures, rather than silently widening outbound browser access.

Policy-blocked requests are not counted as site failures. They are reported separately with an expected reason such as `read_only_mutation`, `browser_api_recursion` or `origin_not_allowlisted`.

### Important read-only boundary

Read-only mode blocks `POST`, `PUT`, `PATCH`, `DELETE` and same-origin WebSockets at the browser network layer. It cannot repair an application endpoint that incorrectly mutates state through `GET`/`HEAD`; TaskForge APIs must preserve normal HTTP method semantics.

Authenticated contexts receive the caller's existing access token. They do not receive the caller's refresh token. Keep the access-token lifetime longer than the configured session lifetime (the current defaults are 120 and 110 minutes), or create a new browser session after token expiry.

Artifact limits are separate:

```text
Browser:MaxCachedArtifactBytes      default 4 MiB
Browser:MaxArtifactResponseBytes    default 32 MiB
```

A valid render may be returned without being cached when it exceeds the cache limit. A render over the response limit is rejected with HTTP 413.

Public crawler captures have an independent bounded deadline (`Browser:CaptureTimeoutSeconds`, default 75 seconds) and a dedicated global work cap (`Browser:MaxConcurrentPublicCaptures`, default 2), but the discovery path is optimized for short external crawler deadlines: it uses `Browser:AgentCaptureWaitMilliseconds` (default 150 ms), persists snapshot JSON + PNG synchronously, and leaves PDF to the explicit `/api/site/render.pdf` endpoint. Navigation waits for DOM readiness, then a bounded `data-taskforge-ready`/mounted-root signal and font stabilization rather than unbounded `networkidle`. A true server timeout returns a structured HTTP 504 diagnostic including the stage, safe URL, readiness state and pending requests; a caller disconnect is logged separately and is not reported as an internal server failure.

## Rate-limit response contract

Successful and rejected Browser API calls expose:

```text
RateLimit-Limit
RateLimit-Remaining
RateLimit-Reset        seconds until reset
RateLimit-Policy       for example 10;w=60
X-RateLimit-Limit
X-RateLimit-Remaining
X-RateLimit-Reset      Unix reset timestamp, compatibility only
Retry-After            on HTTP 429
```

The frontend CORS policy exposes these headers together with artifact, session, render-size and cache metadata. Discovery publishes `recommendedCaptureConcurrency: 1`; callers should avoid parallel Chromium captures unless there is a concrete need. Excess unique public captures wait only `Browser:PublicCaptureQueueWaitMilliseconds` (default 3000 ms) before a fast HTTP 429, so distributed crawler IPs cannot build an unbounded Chromium queue.

## Scale model

Live Playwright sessions are process-local. Keep a single `browser-api` replica for interactive sessions unless sticky routing or external session ownership is added. Stateless `/api/site/*` requests can be cached through Redis and are safe to scale separately in a later split.

## Operations

Local service build:

```bash
./build.sh browser-api gateway identity-api front front-ct
```

In production the gateway owns Docker-network aliases for `DOMAIN` and `CT_DOMAIN`. Chromium therefore reaches the real production gateway and TLS virtual host directly inside the Compose network instead of relying on public-IP hairpin NAT. The gateway restores `CF-Connecting-IP` only when the TCP peer belongs to Cloudflare's published proxy ranges, then forwards the resulting trusted address as `X-TaskForge-Client-IP` for application rate limiting. Direct origin requests keep their real socket address.

Production smoke test after deployment:

```bash
./scripts/prod/check-browser-api.sh https://taskforge.by
```

Authenticated smoke test:

```bash
TASKFORGE_BROWSER_ACCESS_TOKEN='...' \
TASKFORGE_BROWSER_PATH='/courses' \
./scripts/prod/check-browser-api.sh https://taskforge.by
```

Static security invariants:

```bash
bash scripts/security/check-browser-api-security.sh
```


Public `/ai-artifacts/*` responses are explicitly `noindex, noarchive`; a syntactically valid artifact ID that is no longer available returns HTTP 410 so crawler caches do not report ordinary TTL expiry as a broken persistent resource. `robots.txt` additionally keeps known traditional SEO/search crawlers such as MJ12bot, AhrefsBot and SemrushBot away from temporary artifact paths while leaving the dedicated AI-agent flow available.


## Agent-friendly interactive workflow

`GET /api/site/agent/playbook` is the concise machine workflow for AI onboarding and solving assignments. Interactive Browser API pages expose stable `automationId`, `automationRole`, `automationAction`, `automationState` and `automationKind` metadata in semantic snapshot v2.1. `<select>` controls also expose exact option `value`/label pairs and form fields expose their current value.

Browser API Chromium contexts set `window.__TASKFORGE_BROWSER_AUTOMATION__ = true`; the solve page responds by exposing the code editor as a normal textarea instead of Monaco, so `fill` is deterministic. Session snapshots accept `waitMs` up to 15000 ms, authenticated sessions default to 45 minutes idle / 110 minutes absolute lifetime, and public artifacts default to a one-hour TTL.
