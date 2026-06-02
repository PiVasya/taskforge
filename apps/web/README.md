# apps/web — frontend TaskForge

React + Tailwind + Monaco SPA for the main TaskForge site.

## Runtime rule

The browser must talk to the backend through same-origin URLs only:

```text
/api/*
/hubs/*
```

Do not hardcode `localhost`, `taskforge.by`, `ct.taskforge.by`, gateway ports, or direct microservice URLs in frontend code. In dev the public origin is usually `http://localhost:18080`; in production it is `https://taskforge.by`. The gateway owns all routing.

## Local development

```bash
cd apps/web
npm ci
npm start
```

For the full product use Docker Compose from the repository root:

```bash
./deploy/dev/compose.sh build --no-cache front gateway
./deploy/dev/compose.sh up-logs
```

## Production build

```bash
npm run build
```
