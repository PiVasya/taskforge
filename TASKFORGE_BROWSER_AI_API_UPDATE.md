# TaskForge Browser + AI Accounts Update

## Result

TaskForge now exposes its already deployed interfaces to automated agents without rebuilding the frontend and without special `/agent-view/TOKEN` links.

The update adds two related capabilities:

1. ordinary TaskForge accounts can be marked as `human` or `ai`;
2. a separate hardened `browser-api` runs real Chromium and exposes public, rate-limited inspection and interaction endpoints for configured TaskForge sites only.

An AI account is **not a role**. `AccountType=ai` is a self-declared account marker used for labels, administration and future analytics. Authorization continues to depend on the existing role/permission model.

## Public entry points

```text
GET  /.well-known/taskforge-ai.json
GET  /llms.txt
GET  /api/browser/openapi.json
GET  /api/site/info
GET  /api/site/routes
GET  /api/site/snapshot
GET  /api/site/render
GET  /api/site/render.pdf
POST /api/browser/sessions
```

Examples:

```text
https://taskforge.by/api/site/snapshot?path=/courses&width=390&height=844
https://taskforge.by/api/site/render?path=/courses&width=390&height=844&fullPage=true
https://taskforge.by/api/site/render?path=/courses&width=390&height=844&annotated=true
https://taskforge.by/api/site/render.pdf?path=/courses&width=1440&height=900
```

Use `site=ct` to inspect the CT frontend.

## AI account registration

API registration accepts one additional optional field:

```json
{
  "login": "taskforge-ui-agent",
  "password": "a-strong-unique-password",
  "firstName": "UI agent",
  "lastName": "",
  "accountType": "ai"
}
```

Allowed values:

```text
human
ai
```

Missing `accountType` defaults to `human`. The main and CT registration pages also understand:

```text
/register?accountType=ai
```

AI users are marked in profile/admin UI. Administrators can filter and edit account type independently of role.

## Browser API architecture

```text
Internet
   |
   v
TaskForge gateway / Nginx
   |
   +--> identity-api (ordinary registration/login/JWT)
   |
   +--> browser-api
           |
           +--> shared headless Chromium
           +--> isolated browser context per stateless request/session
           +--> taskforge.by or ct.taskforge.by
           +--> Redis rate limits and short public artifact cache
```

In production the gateway also receives Docker-network aliases for `DOMAIN` and `CT_DOMAIN`. Chromium keeps the public HTTPS hostname and certificate semantics, but reaches the gateway directly through the Compose network instead of depending on public-IP hairpin routing.

The service does not accept an arbitrary URL. Every request contains:

- a configured site key (`main` or `ct`);
- a relative path such as `/courses`;
- a bounded viewport and wait time.

## Semantic snapshot

A snapshot contains:

- page URL and title;
- visible text and headings;
- Playwright AI-mode ARIA snapshot with boxes when available;
- viewport/document dimensions and current scroll position;
- horizontal-overflow diagnostics and offending elements;
- interactive elements with `tf1`, `tf2`, ... IDs and bounds;
- small touch targets, missing labels, missing image alt text, duplicate IDs and heading issues;
- console errors, failed network requests and HTTP errors;
- navigation, FCP, LCP, CLS, transfer and long-task measurements;
- truncation metadata when limits are reached.

`annotated=true` places the same `tfN` labels onto the PNG.

## Interactive browser sessions

Create a read-only session:

```http
POST /api/browser/sessions
Content-Type: application/json

{
  "site": "main",
  "path": "/courses",
  "width": 390,
  "height": 844,
  "readOnly": true,
  "waitMs": 800
}
```

The response includes a session ID and a cryptographically random token. Send the token on every operation:

```http
X-TaskForge-Browser-Session-Token: <token>
```

Available operations:

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

Actions use `elementId` from the latest snapshot, not a caller-provided CSS selector or JavaScript expression.

Anonymous sessions are always read-only. `readOnly=false` requires an ordinary valid TaskForge access token and receives only that account's existing permissions.

## Authentication model

Authenticated inspection calls accept:

```http
Authorization: Bearer <ordinary TaskForge access token>
```

The Browser API validates issuer, audience, lifetime, token type, signature and HMAC-SHA256 algorithm with the same JWT key as `identity-api`.

The token is injected into the isolated Chromium context as the existing `tf_at` cookie and as a one-time frontend bootstrap value. The main and CT auth providers consume that bootstrap value before loading the profile.

Refresh tokens are deliberately not copied into Chromium. With current defaults, the access-token lifetime is 120 minutes while a browser session expires after at most 30 minutes.

## Security boundaries

### SSRF and outbound access

- no request contract exposes a general `url` field;
- only configured TaskForge origins can be top-level documents;
- subresources are restricted to TaskForge origins plus explicit `BROWSER_ALLOWED_EXTERNAL_ORIGINS`;
- schemes such as `file:`, custom protocols and arbitrary network origins are blocked;
- popups are closed;
- downloads and service workers are disabled;
- browser pages cannot call `/api/site` or `/api/browser`, preventing recursive render/session amplification;
- double-encoded path traversal, backslashes, control characters and Browser API paths are rejected.

### Read-only mode

Read-only Chromium contexts abort unsafe same-origin HTTP methods and WebSockets. They also block unsafe cross-origin requests. This is a network-layer safety boundary; application endpoints must still never mutate state through `GET` or `HEAD`.

### Resource limits

Defaults:

```text
concurrent Chromium operations       4
active sessions                       8
anonymous sessions per owner          2
authenticated sessions per owner      3
session idle lifetime                 10 minutes
session absolute lifetime             30 minutes
viewport                              320..2560 x 320..1440
full-page height                      20000 CSS px
screenshot pixels                     24000000
cached artifact                       4 MiB
returned render artifact              32 MiB
```

A render over the response limit returns HTTP 413. A valid artifact larger than the cache limit is returned but not cached.

### Rate limiting

Nginx applies cheap request/connection limits before proxying. Application limits use atomic Redis Lua `INCR` + first-use `EXPIRE`, with a process-local emergency fallback.

Default public limits per caller:

```text
metadata            120 / minute
snapshot             30 / minute
render               10 / minute
session create        5 / minute
session actions     120 / minute
```

A second network-level bucket prevents many identities behind one address from bypassing limits.

Registration/auth rate limiting in `identity-api` was also moved from process-local queues to Redis-backed atomic windows. Registration is limited per network+identity, per network/hour and per network/day without CAPTCHA, so legitimate agents can register while disposable-account floods are bounded.

### Container hardening

The production Browser API container:

- runs as Playwright's non-root `pwuser`;
- uses a read-only root filesystem;
- drops all Linux capabilities;
- enables `no-new-privileges`;
- has no Docker socket;
- has bounded CPU, RAM, PIDs, `/tmp` and shared memory;
- publishes no host port and is reachable only through the gateway.

## Redis cache and scale

Only anonymous stateless artifacts are cached. Authenticated page contents never enter the shared public artifact cache.

Interactive sessions are process-local. Keep one `browser-api` replica unless sticky routing or external session ownership is implemented. Anonymous read-only session authorization is based on the high-entropy session token rather than a source IP; authenticated sessions additionally bind to the TaskForge user identity. Stateless inspection can later be split/scaled independently.

## Changed areas

```text
services/browser/api/                         new Chromium service
services/identity/api/Domain/IdentityUser.cs  AccountType
services/identity/api/...                     registration/JWT/profile/admin/rate limits
apps/web, apps/web-ct                         AI registration bootstrap and labels
apps/gateway                                  routes and Nginx shields
deploy/dev, deploy/prod                       browser service/config/resources
.github/workflows                             browser image and security checks
scripts/prod/check-browser-api.sh             production smoke test
scripts/security/check-browser-api-security.sh architecture invariants
```

## Required EF migration

No migration or `ModelSnapshot` was generated in this update. Generate it from the repository root:

```bash
./scripts/generate-migrations.sh AddAiAccountType identity
```

Direct fallback:

```bash
dotnet ef migrations add AddAiAccountType \
  --project services/identity/api/TaskForge.Identity.Api.csproj \
  --startup-project services/identity/api/TaskForge.Identity.Api.csproj \
  --context IdentityDbContext \
  --output-dir Migrations
```

Review the generated migration. It should add a required `AccountType` column with the `human` default and the configured index, preserving all existing users as human accounts.

## Production rollout

1. Generate and commit `AddAiAccountType`.
2. Push the branch and wait for the images `browser-api`, `identity-api`, `gateway`, `front` and `front-ct` to be built.
3. Copy/sync the updated repository or at minimum the updated `deploy/prod` folder to the server.
4. Merge new keys from `deploy/prod/.env.example` into the server's real `deploy/prod/.env`. `prepare-env.sh`/`deploy.sh` performs template synchronization without replacing existing secrets.
5. Validate configuration:

```bash
./scripts/prod/check-prod-config.sh
```

6. Apply the update:

```bash
./deploy/prod/deploy.sh
```

For an explicit manual pull/recreate:

```bash
./deploy/prod/compose.sh pull browser-api identity-api gateway front front-ct
./deploy/prod/compose.sh up -d browser-api identity-api gateway front front-ct
```

7. Inspect status/logs:

```bash
./deploy/prod/compose.sh ps
./deploy/prod/compose.sh logs --tail=250 browser-api gateway identity-api
```

8. Run public smoke tests:

```bash
./scripts/prod/check-browser-api.sh https://taskforge.by
```

9. Verify manually:

```bash
curl -fsS https://taskforge.by/.well-known/taskforge-ai.json
curl -fsS https://taskforge.by/llms.txt
curl -fsS https://taskforge.by/api/site/info
curl -fsS 'https://taskforge.by/api/site/snapshot?path=/&width=390&height=844' > /tmp/taskforge-snapshot.json
curl -fsS 'https://taskforge.by/api/site/render?path=/&width=390&height=844' > /tmp/taskforge-mobile.png
```

## Production environment additions

The update adds `BROWSER_*` keys to both dev and production templates. Important production values:

```dotenv
BROWSER_MAIN_ORIGIN=https://taskforge.by
BROWSER_CT_ORIGIN=https://ct.taskforge.by
BROWSER_ALLOWED_EXTERNAL_ORIGINS=https://s3.taskforge.by
BROWSER_IGNORE_HTTPS_ERRORS=false
BROWSER_RATE_LIMITS_ENABLED=true
BROWSER_MAX_CACHED_ARTIFACT_BYTES=4194304
BROWSER_MAX_ARTIFACT_RESPONSE_BYTES=33554432
BROWSER_MAX_CONCURRENT_OPERATIONS=4
BROWSER_MAX_ACTIVE_SESSIONS=8
BROWSER_CPUS=2.0
BROWSER_MEM_LIMIT=2g
BROWSER_PIDS_LIMIT=256
BROWSER_SHM_SIZE=512m
BROWSER_TMPFS_SIZE=1g
```

Do not add untrusted external origins merely to hide snapshot network errors. Allowlist only resources controlled and required by TaskForge.

## Validation commands

```bash
bash scripts/security/check-browser-api-security.sh
bash scripts/prod/check-browser-api.sh https://taskforge.by
python3 scripts/ci/check-workflow-integrity.py
bash scripts/ci/check-workflow-matrix.sh
git diff --check
```
