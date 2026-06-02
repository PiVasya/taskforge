# Legacy routing compatibility recheck

The legacy monolith used a single nginx edge gateway in front of the SPA and API services. The browser called same-origin relative URLs such as `/api/...` and `/hubs/...`, so normal site traffic did not require CORS.

The microservice gateway keeps the same external contract:

- `taskforge.by` serves the main frontend.
- `ct.taskforge.by` serves the CT frontend.
- both domains proxy `/api/...` through the same edge gateway to the correct internal microservice.
- both domains proxy `/hubs/...` through the same edge gateway to the correct internal hub service.
- frontend code must keep using relative `/api` and `/hubs` URLs.

Hardening performed in this update:

- API fallback changed from `location ^~ /api/` to `location /api/` so it no longer preempts regex service routes.
- Hub locations now include the common proxy header snippet explicitly, because nginx does not inherit server-level `proxy_set_header` directives when a location defines its own `proxy_set_header` values.
- Gateway now maps public forwarded protocol/port explicitly, preserving `localhost:18080` in dev and `taskforge.by` / `ct.taskforge.by` in production.
- Production compose gateway default now matches docs: `GATEWAY_MODE=auto`.

