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
```

The discovery document tells an agent how to register an ordinary account with `accountType: "ai"`, how to inspect public pages and how to create an interactive browser session.

## Stateless inspection

```text
GET /api/site/snapshot?path=/courses&width=390&height=844
GET /api/site/render?path=/courses&width=390&height=844&fullPage=true
GET /api/site/render?path=/courses&width=390&height=844&annotated=true
GET /api/site/render.pdf?path=/courses&width=1440&height=900&fullPage=true
```

Use `site=ct` for the CT frontend. Add an ordinary TaskForge access token as `Authorization: Bearer ...` to render pages available to that user. Browser API authentication is intentionally explicit: ambient `tf_at` cookies are ignored. Stateless renders are always read-only.

A semantic snapshot includes:

- visible page text and headings;
- Playwright AI-mode ARIA snapshot with element boxes when supported;
- viewport and document dimensions;
- horizontal overflow and offending elements;
- interactive controls with `tf1`, `tf2`, ... references and bounds;
- touch-target, label, heading, image-alt and duplicate-ID diagnostics;
- console errors, failed requests and HTTP errors;
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

The response contains a random high-entropy session token. Send it on every subsequent session request:

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

Actions use `elementId` values from the latest snapshot, not arbitrary CSS selectors. Obtain a new snapshot after navigation or a major DOM change because references are intentionally scoped to the current page state.

Anonymous sessions are always read-only. `readOnly=false` requires a valid ordinary TaskForge access token and still grants only that account's existing permissions.

## Security model

- Input contains a configured site key plus a relative TaskForge path. Request contracts expose no arbitrary URL field.
- Chromium document navigation cannot leave the selected TaskForge origin.
- Subresources are limited to configured TaskForge origins and explicit `Browser:AllowedExternalOrigins` entries.
- `/api/site` and `/api/browser` cannot recursively render themselves.
- Read-only contexts abort every non-safe same-origin HTTP request, not only `/api/*` calls.
- Popups are closed; service workers and downloads are disabled.
- Nginx applies an inexpensive request/connection shield before the request reaches the service.
- Application quotas use atomic Redis `INCR` + `EXPIRE` Lua scripts, with a process-local emergency fallback.
- Chromium operations, active sessions, per-owner sessions, viewport, full-page pixels, wait time, response bytes and session lifetime are bounded.
- Session tokens are stored only as SHA-256 hashes and compared in constant time.
- Console and network diagnostics remove query strings and redact common token/password forms.
- Authenticated snapshots and renders are never shared through the public artifact cache.
- The container runs as `pwuser`, has a read-only root filesystem, drops all Linux capabilities, has no Docker socket and receives CPU/RAM/PID/temp/shm limits.

Third-party images are intentionally blocked unless their origin is explicitly allowlisted. Those failures remain visible in snapshot diagnostics rather than silently widening outbound browser access.

### Important read-only boundary

Read-only mode blocks `POST`, `PUT`, `PATCH`, `DELETE` and same-origin WebSockets at the browser network layer. It cannot repair an application endpoint that incorrectly mutates state through `GET`/`HEAD`; TaskForge APIs must preserve normal HTTP method semantics.

Authenticated contexts receive the caller's existing access token. They do not receive the caller's refresh token. Keep the access-token lifetime longer than the configured session lifetime (the current defaults are 120 and 30 minutes), or create a new browser session after token expiry.

Artifact limits are separate:

```text
Browser:MaxCachedArtifactBytes      default 4 MiB
Browser:MaxArtifactResponseBytes    default 32 MiB
```

A valid render may be returned without being cached when it exceeds the cache limit. A render over the response limit is rejected with HTTP 413.

## Scale model

Live Playwright sessions are process-local. Keep a single `browser-api` replica for interactive sessions unless sticky routing or external session ownership is added. Stateless `/api/site/*` requests can be cached through Redis and are safe to scale separately in a later split.

## Operations

Local service build:

```bash
./build.sh browser-api gateway identity-api front front-ct
```

In production the gateway owns Docker-network aliases for `DOMAIN` and `CT_DOMAIN`. Chromium therefore reaches the real production gateway and TLS virtual host directly inside the Compose network instead of relying on public-IP hairpin NAT.

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
