# apps/web-ct — CT frontend

React SPA for the CT branch of the site.

## Runtime rule

All API and websocket calls must be same-origin:

```text
/api/*
/hubs/*
```

In production this app is served from `https://ct.taskforge.by`; in dev it can be served through the same gateway using `CT_DOMAIN` if mapped locally. Do not hardcode direct backend hosts in frontend code.
