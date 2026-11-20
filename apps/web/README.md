# apps/web — frontend TaskForge

React + Tailwind + Monaco SPA for the main TaskForge site.

## Runtime rule

The browser must talk to the backend through same-origin URLs only:

```text
/api/*
/hubs/*
```

Do not hardcode backend hosts, gateway ports, or direct microservice URLs in frontend code. In the local K3s lab the gateway is available at `http://127.0.0.1:18080` via port-forwarding. The gateway owns API routing.

## Local development

```bash
cd apps/web
npm ci
npm start
```

For the full local product use the isolated K3s deployment described in [`docs/operations/README_K3S_BRANCH.md`](../../docs/operations/README_K3S_BRANCH.md). The old Compose entrypoints are not part of this branch.

## Production build

```bash
npm run build
```
