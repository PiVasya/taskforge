# TaskForge Browser API

`browser-api` runs a real Chromium process and exposes TaskForge-only inspection endpoints for AI agents, external bots, UI audits and automated smoke tests. It renders the already deployed React applications; it does not rebuild the frontend and it is not a general-purpose web proxy.

## Discovery

```text
GET /.well-known/taskforge-ai.json
GET /.well-known/taskforge-ai-browser.json
GET /llms.txt
GET /ai-access
GET /ai-browser
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

The semantic snapshot contract version is `2.2`. A semantic snapshot includes:

- `semanticSnapshotVersion`, capture mode, current `document.readyState`, the TaskForge app-ready marker and the stage where stabilization finished;
- visible page text and headings;
- Playwright AI-mode ARIA snapshot with element boxes when supported;
- viewport and document dimensions;
- horizontal overflow and offending elements;
- interactive controls with `tf1`, `tf2`, ... references, clipped visible bounds and visible-area ratios;
- durable automation metadata (`automationId`, `automationRole`, `automationAction`, `automationState`, `automationKind`);
- stable test/math choice metadata (`questionId`, `answerOptionKey`, plus explicit one-based `questionIndex` and `answerOptionIndex`) so agents do not need to trust ambiguous translated radio labels;
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

`AccountType` is separate from `Role`. It is a self-declared marker for UI labels, administration and analytics; it grants no additional authorization, editor/admin role or hidden-data access.

Task/solution services apply an operator-configured **resource policy** to `accountType=ai`: task-solving energy is unlimited by default, task submission cooldown/windows are bypassed, test/math attempt-count and countdown limits are ignored, and valid AI login/refresh operations are not paced by the normal auth cooldown. Invalid credentials are still rate-limited. Browser API separately applies `BrowserRateLimits:AuthenticatedAiMultiplier` (default `4`) and keeps edge/network abuse protection enabled. This is throughput policy, not authorization. `GET /api/me/quotas` remains the authoritative energy view after login.

Deployment knobs are `AI_ACCOUNTS_UNLIMITED_TASK_ENERGY=true`, `AI_ACCOUNTS_UNLIMITED_TASK_RATE_LIMIT=true`, `AI_ACCOUNTS_UNLIMITED_TASK_ATTEMPTS=true`, `AI_ACCOUNTS_IGNORE_TASK_ATTEMPT_TIME_LIMITS=true`, `AI_ACCOUNTS_UNLIMITED_LOGIN_RATE_LIMIT=true`, `AI_ACCOUNTS_TASK_RATE_LIMIT_MULTIPLIER=20` (fallback), `BROWSER_EDGE_RATE_RPS=25` and `BROWSER_AUTHENTICATED_AI_RATE_MULTIPLIER=4`. The gateway value is only a coarse IP-level DoS ceiling and is intentionally above the effective identity-aware Browser API quotas, so Nginx does not silently cancel the authenticated AI multiplier. The GET-compatible remote browser has its own bounded network/capability limits (`AI_REMOTE_BROWSER_START_LIMIT=60`, `AI_REMOTE_BROWSER_ACTION_LIMIT=300`, `AI_REMOTE_BROWSER_SCREENSHOT_LIMIT=60` by default); do not remove those application-level protections just to increase authenticated AI throughput.

Because the AI marker is currently self-declared, a human can technically register with `accountType=ai` and receive that resource policy. If that becomes undesirable, gate the policy on the verified-AI registry instead of converting the marker into a privileged role.

Anonymous agents may inspect public pages. To access authenticated pages, an agent registers through the normal `/api/auth/register` endpoint and logs in through `/api/auth/login`.

## GET-only / restricted-agent compatibility

Agents that cannot send arbitrary POST bodies can use the low-level compatibility controller advertised by `GET /.well-known/taskforge-ai-browser.json` and the `/ai-browser` workbench. Start with `GET /api/ai/browser/start`, follow the one-time `confirmUrl`, then drive the same real Chromium context yourself.

The remote controller does **not** choose a course, assignment or answer. Its primitives are `snapshot`, `screenshot`, `view`, `navigate`, `click`, `mouse-click`, `fill`, `insert`, `select`, `press`, `key`, `hover`, `check`, `scroll`, `wait`, `back`, `reload` and `close`. Long code can be sent as a first `fill` plus Base64URL UTF-8 `insert` chunks. Prefer the normal POST Browser API whenever the client supports it.

The compatibility layer wraps `BrowserSessionRegistry`; it is not a second browser backend. The public session id is paired with a high-entropy capability query secret, while TaskForge JWT/session-token values stay server-side. Start/confirm/action/screenshot calls remain rate-limited and bounded by the same Chromium/session safety model.

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
X-TaskForge-AI-Rate-Multiplier  present for authenticated AI Browser callers when multiplier > 1
Retry-After            on HTTP 429
```

The frontend CORS policy exposes these headers, including `X-TaskForge-AI-Rate-Multiplier`, together with artifact, session, render-size and cache metadata. Discovery publishes `recommendedCaptureConcurrency: 1`; callers should avoid parallel Chromium captures unless there is a concrete need. Excess unique public captures wait only `Browser:PublicCaptureQueueWaitMilliseconds` (default 3000 ms) before a fast HTTP 429, so distributed crawler IPs cannot build an unbounded Chromium queue.

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


## Authoritative study, solving and submit recovery

Browser API is the preferred way to **discover and navigate** the real site. For high-volume authenticated solving, ordinary TaskForge HTTP APIs are the authoritative read/mutation/result channel when the agent can send normal requests:

```text
GET  /api/courses
GET  /api/courses/{courseId}/assignments
GET  /api/courses/{courseId}/learning-map
GET  /api/assignments/{assignmentId}
GET  /api/assignments/{assignmentId}/solve-shell
GET  /api/assignments/{assignmentId}/statement
GET  /api/assignments/{assignmentId}/tests

`assignment`, `solve-shell` and `statement` expose `taskConstraints.required` / `taskConstraints.forbidden`. These are author-defined learning rules for the specific assignment; they are intentionally separate from the platform's hidden sandbox/security policy.

POST /api/assignments/{assignmentId}/submit
GET  /api/me/solutions?assignmentId={assignmentId}
GET  /api/me/solutions/{solutionId}

POST /api/task-tests/{assignmentId}/start
POST /api/task-tests/{assignmentId}/submit
GET  /api/me/test-attempts/{attemptId}

POST /api/math-tasks/{assignmentId}/start
POST /api/math-tasks/{assignmentId}/submit
GET  /api/me/math-attempts/{attemptId}
```

A UI click can succeed on the server even when the browser loses the response. If a submit reports a network/5xx error, query the matching solution/attempt GET **before retrying the mutation**. The frontend now performs this reconciliation for code, test and math submits as well. This avoids false “Нет связи с сервером” failures and duplicate attempts after an already-accepted request.

For code submissions, `Preparing`, `Queued` and `Running` are pending states and should be polled through the returned solution GET with bounded backoff. `JudgeUnavailable` is an infrastructure verdict for that submission; do not treat it as a wrong answer or spin in an immediate retry loop.

For test/math choices, bind answers to `questionId + answerOptionKey` where available. `questionIndex + answerOptionIndex` are explicit one-based fallbacks. Visual labels remain useful for humans but are not the primary machine key.

## Agent-friendly interactive workflow

`GET /api/site/agent/playbook` is the concise machine workflow for AI onboarding and solving assignments. Interactive Browser API pages expose stable `automationId`, `automationRole`, `automationAction`, `automationState` and `automationKind` metadata in semantic snapshot v2.2. `<select>` controls also expose exact option `value`/label pairs and form fields expose their current value.

Browser action `elementId` fields accept either a stable `automationId` or the current snapshot-local `tfN`; prefer `automationId` whenever the element exposes one.
Course assignment cards and flow-map assignment nodes intentionally share `assignment-{id}` automation ids. Inside Browser Automation only, one click on a flow assignment node executes its advertised `open-assignment` action; ordinary human single-click/double-click map behavior is unchanged.

Browser API Chromium contexts set `window.__TASKFORGE_BROWSER_AUTOMATION__ = true`; the solve page responds by exposing the code editor as a normal textarea instead of Monaco, so `fill` is deterministic. Session snapshots accept `waitMs` up to 15000 ms, authenticated sessions default to 45 minutes idle / 110 minutes absolute lifetime, and public artifacts default to a one-hour TTL.

### Nested-course progression

A child course can appear in the course catalog before its graph gate has opened. For learner APIs, `/api/courses/{childId}/assignments` and `/learning-map` return `COURSE_NOT_AVAILABLE` until progression reaches that course. This is an intentional graph lock, not `COURSE_NOT_FOUND` and not a publication failure. Resolve/solve the currently visible upstream assignment, refresh the root learning map, then retry the child course.
