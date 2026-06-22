# TaskForge HA bootstrap

This is the first, deliberately small HA step.

The goal is not to promote a standby node yet. The goal is to give Cloudflare a
safe endpoint that can distinguish an active primary node from a standby node.

## Endpoint

Gateway exposes:

```text
GET /ha/live
GET /ha/primary-ready
```

`/ha/live` only checks that the gateway container can answer.

`/ha/primary-ready` is the endpoint intended for Cloudflare monitoring:

- `TASKFORGE_NODE_ROLE=primary` -> HTTP 200
- `TASKFORGE_NODE_ROLE=standby` -> HTTP 503

That means a standby node can be online but still hidden from production traffic.

## Primary .env

```env
TASKFORGE_NODE_ROLE=primary
```

## Standby .env

```env
TASKFORGE_NODE_ROLE=standby
```

## Local checks

```bash
curl -i http://127.0.0.1/ha/live
curl -i http://127.0.0.1/ha/primary-ready
```

For HTTPS production:

```bash
curl -i https://taskforge.by/ha/primary-ready
```

## Cloudflare first monitor

Use `/ha/primary-ready`, not `/health/gateway`.

`/health/gateway` only says that nginx is alive. `/ha/primary-ready` says that
this node is allowed to receive live traffic.

## Next steps

1. Add standby server with `TASKFORGE_NODE_ROLE=standby`.
2. Point `primary.<domain>` at the primary server and `standby.<domain>` at the standby server.
3. Configure Cloudflare monitors against `/ha/primary-ready`.
4. Only after that add promote scripts for PostgreSQL, MinIO and singleton services.
