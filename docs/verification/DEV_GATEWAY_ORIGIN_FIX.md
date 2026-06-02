# Dev gateway origin / CORS fix

Fixed local browser requests being redirected from `localhost:18080` to `localhost` without the dev port.

Changes:

- `deploy/dev/compose/10-apps-gateway.yaml` now uses `GATEWAY_MODE=dev` instead of the production `http` template.
- Gateway templates preserve the browser host including port by forwarding `Host: $http_host`.
- Gateway templates rewrite upstream absolute `Location` headers back to the current browser origin.
- Gateway templates normalize `/api/.../` trailing slash requests internally instead of relying on browser-visible redirects.
- Frontend Caddyfiles no longer use `{path}/` in `try_files`, avoiding canonical slash redirects for SPA pseudo-routes.
- `scripts/verify-structure.sh` now checks the dev gateway origin rules.

This addresses browser-side CORS failures such as requests leaving `http://localhost:18080` and going to `http://localhost/api/...`.
