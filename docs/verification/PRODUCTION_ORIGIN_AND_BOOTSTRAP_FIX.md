# Production origin and admin bootstrap hardening

This update replaces the previous ad-hoc gateway fix with a stricter production/dev origin policy.

## Fixed

- Gateway routing is now centralized in nginx snippets instead of duplicated in every template.
- Dev gateway always runs with `GATEWAY_MODE=dev`.
- Nginx preserves `$http_host`, so `localhost:18080` is not rewritten to `localhost`.
- API trailing slash normalization is internal and does not create browser-visible redirects.
- SPA Caddy config no longer tries `{path}/`, avoiding directory canonical redirects.
- Production `.env.example` uses `taskforge.by` / `ct.taskforge.by`.
- Production S3 public endpoint is required; it no longer falls back to `http://localhost:9000`.
- First-user-admin behavior is now explicit through bootstrap env variables.

## Required production values

```text
DOMAIN=taskforge.by
CT_DOMAIN=ct.taskforge.by
GATEWAY_MODE=auto
S3_PUBLIC_ENDPOINT=https://s3.taskforge.by
BOOTSTRAP_FIRST_USER_IS_ADMIN=false
BOOTSTRAP_ADMIN_EMAILS=admin@taskforge.by
```

## Browser rule

Frontend code must call backend endpoints through same-origin paths only:

```text
/api/*
/hubs/*
```

No direct `localhost`, `taskforge.by`, `ct.taskforge.by`, or microservice URL should be used in frontend API clients.
