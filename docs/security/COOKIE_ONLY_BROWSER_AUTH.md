# Cookie-only browser authentication

## Scope

This source update removes the duplicate JavaScript copy of the TaskForge JWT from
both `apps/web` and `apps/web-ct`. It does not replace JWT with an opaque server-side
session, rotate signing keys, revoke existing credentials, change database models,
or modify the standalone cluster package. Server-side token revocation is a
separate, still-open requirement.

The independent `taskforge-server-v41.4.0(1).tar.gz` remains unchanged. Do not use the
older embedded deployment bundle as a substitute for that standalone package.

## Browser contract

Identity sends access credentials only through the existing `tf_at` HttpOnly
cookie and, for remembered devices, refresh credentials through `tf_rt`. Cookie
names, path `/`, SameSite=Lax, lifetime configuration and HTTPS Secure behavior
are preserved. The development HTTP behavior is not changed.

`POST /api/auth/login`, `POST /api/auth/refresh`, and `GET /api/auth/session` return
only the following session envelope:

```json
{
  "authenticated": true,
  "expiresAtUtc": "2026-10-16T12:00:00Z",
  "rememberMe": false,
  "user": {
    "id": "ordinary-user-id",
    "role": "User",
    "roles": ["User"]
  }
}
```

The example expiry/profile are illustrative; real values come from Identity.
The `user` object retains the existing profile contract. No access or refresh JWT
is returned in this envelope. `/api/auth/session` describes an existing valid
access cookie; it does not renew it. The request has `Cache-Control: no-store` on
the response. `/api/auth/remember` uses the access cookie and returns preference
metadata rather than requiring a duplicate Bearer credential.

Both frontends now keep `user`, `isAuthenticated`, `expiresAtUtc`, and the
remember-device preference instead of an `access` JWT. UI roles come from the
profile; backend authorization remains authoritative. There is no JWT decoding,
access-token module state, token factory, or page-global token injection.

The existing localStorage remember flag is not an authentication credential. A
new cross-tab event stores only an event kind and a random nonce, not a token or
profile. The original implementation kept its duplicate JWT primarily in memory;
this change should not be described as removing an existing localStorage JWT.

## Request protection

The shared browser HTTP client forces credentialed requests and removes stale
Authorization headers. Unsafe browser requests carry `X-TaskForge-CSRF: 1`.
This is a non-secret request marker, NOT a token or second login credential.
Raw activity/keepalive fetch calls carry it too. Activity unload uses keepalive
fetch rather than sendBeacon, because the latter cannot carry the required header.

The existing request-security middleware in all 13 applicable APIs checks unsafe
cookie-authenticated requests and all unsafe `/api/auth` requests, including
login with no existing cookie. It requires the custom header, rejects foreign
Origin values and cross-site/same-site Fetch Metadata values, and compares an
Origin to the public Host/scheme preserved by the trusted gateway. Cross-origin
browser deployments are not supported by this policy.

WebSocket and hub GET handshakes use same-origin validation without requiring an
impossible browser WebSocket custom header. Negotiation/unsafe transport calls
send the header. Query-string access JWT authentication is removed. Only
`token_type=access` with the expected signing algorithm is accepted for access
validation; a refresh JWT cannot serve as an access token.

The gateway must continue preserving the public Host and overwriting
X-Forwarded-Proto. Directly exposed internal services and untrusted forwarder
headers are not an alternative deployment model for this code.

The AI service's existing delegated internal requests preserve the incoming
CSRF header alongside the user cookie. They do not manufacture a browser Bearer
credential or forward a public Origin to a differently named internal service.

## Realtime and Browser API

Agent, support, Minecraft, course-map presence, and assignment analytics
connections use cookie credentials without `accessTokenFactory`. Course-map
connection reuse is keyed by user/course rather than raw JWT. Logout/account
changes stop and discard registered connections and clear existing private
frontend caches. This is client-side cleanup, not server-side revocation of
other clients' already-established connections.

The dedicated public Browser API deliberately retains its explicit server-side
Bearer contract and does not authenticate callers from ambient browser cookies.
Its Chromium page receives a host-scoped HttpOnly cookie, not a JavaScript
window property. A non-browser integration can extract the login cookie through
its HTTP cookie jar/Set-Cookie headers and send an explicit Bearer to Browser API.
Browser JavaScript must never be given that credential. OpenAPI/discovery and the
development judge script have been adapted to the new login response.

## Refresh, logout, and limitations

Cookie-changing operations are serialized within a tab. Refresh requests are
single-flight. With Web Locks support, cookie-changing requests are additionally
serialized across tabs. Logout waits behind a running refresh and rejects new
refreshes while logout is pending. Failed logout is surfaced to the user; the UI
does not claim success before the server confirms cookie removal.

No denylist, session table, token-version check, refresh-family revocation, or
server-side session invalidation is implemented here. A JWT copied before logout
can remain valid until its normal expiry. Clearing HttpOnly cookies is not token
revocation. Browser cleanup cannot retract credentials already held elsewhere.
Cross-tab signaling without Web Locks is best-effort and not a revocation
mechanism. HttpOnly also does not stop an injected script from performing actions
through an already authenticated browser; existing XSS defenses remain essential.

## Compatibility and deployment

This changes the browser auth response contract and the requirements for unsafe
cookie requests. Old JavaScript bundles expect `accessToken` and omit the CSRF
header. A new Identity API with an old frontend, or an old Identity API with the
new frontend, is not a supported transitional combination.

Build and test this release first in staging. Deploy the matching API images,
main frontend, CT frontend and Browser API as a coordinated release through the
existing image deployment process. A maintenance window is safer than allowing
users to operate mixed versions. All affected service-local security middleware
copies must be updated. Reload already-open tabs, and verify login on both sites.
No new cluster service/container, standalone server-bundle update, volume reset,
quorum operation or EF migration is required for these source changes.

For rollback, restore the previous matching application image set together and
reload browser tabs. There is no database rollback associated with this change.
Signing keys and existing cookies are not forcibly invalidated. Do not rotate a
shared JWT key as an unplanned substitute for per-session revocation.

## Verification performed in the editing environment

- 179 Node tests passed across auth, SQL UI, agent UI, admin solutions, landing,
  frontend diagnostics, course map and cluster model suites. These include 33
  new cookie-auth/realtime behavior tests using real production helpers and
  injected/mock transports/builders, not a live web deployment.
- The auth test suite also passed in an isolated main-frontend build context:
  20 tests, without requiring a sibling `apps/web-ct` directory.
- Frontend architecture checks passed (46 SPA routes).
- All 345 source JavaScript/JSX files transpiled syntactically without errors.
  This is not module linking, ESLint, a type check, or a production build.
- `scripts/tests/repository.sh` passed.
- `scripts/security/check-browser-api-security.sh` passed static invariants.
- `scripts/security/check-oj-security.sh` passed all checks available here,
  including Go runner tests/vet; Cargo was unavailable, so its Rust stage was
  skipped by the existing script. Do not interpret its exit 0 as a Rust build.
- Modified shell scripts passed `bash -n`.
- Identity/AI regression tests were added for cookie access, CSRF, session JSON,
  refresh-token rejection, logout cookie deletion and delegated requests, but
  were NOT executed because the .NET SDK is absent.
- Full frontend gate stopped at `npm ci`: the environment cannot resolve the
  npm registry and the required packages are not in the offline cache. CT's
  dependency install is blocked as well. React/Jest/ESLint and production builds
  were therefore NOT completed.
- No live browser/SignalR, Docker-image, HTTPS proxy, production deployment,
  cluster-failover or server-side token-revocation test was performed.

Required follow-up validation on a dependency-capable build host:

```bash
bash scripts/tests/dotnet.sh
bash scripts/tests/frontend.sh
(
  cd apps/web-ct
  npm ci
  CI=true npm test -- --watchAll=false --runInBand
  CI=true npm run build
)
bash scripts/security/check-browser-api-security.sh
bash scripts/security/check-oj-security.sh
```

Dependency-free auth regression check, from the repository root:

```bash
node --test apps/web/scripts/auth-tests/*.test.mjs
```

Before production, verify HTTPS login/reload, remember on/off, session expiry,
parallel 401/refresh, logout during refresh, failed logout, two-tab logout/account
switch, all five realtime consumers, CT roles, AI apply actions, and Browser API
read/mutation sessions. Check that login/session/refresh bodies contain no JWT,
credential cookies are HttpOnly/Secure in HTTPS, outgoing browser requests have
no Bearer/access_token credential, and a cross-origin unsafe request is rejected.
