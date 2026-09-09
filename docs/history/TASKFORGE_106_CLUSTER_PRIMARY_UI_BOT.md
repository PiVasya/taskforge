# TASKFORGE 106 — Cluster Primary control UI + Telegram HA compatibility

Base: `taskforge-develop(206)`.

## Scope

- The `/admin/cluster` dashboard now follows the active TaskForge theme (including neo-brutal mode) instead of using an isolated navy palette.
- Administrators can request a controlled Patroni Primary switchover from the dashboard.
- The UI waits for the complete r57 edge contract: target Primary + application traffic readiness + Cloudflare DNS + public route + TLS `phase=ready`.
- r57 Node Agent events without an `id` are assigned deterministic ids by observability-api instead of being silently skipped.
- Telegram cluster notifications understand the r57 HA/Cloudflare event model and semantically de-duplicate the same leader/edge event emitted by multiple agents.
- Short Node Agent liveness gaps during a known Primary transition are suppressed so they do not become a false `down -> recovered` Telegram pair.

## Primary switch safety

`POST /api/admin/cluster/primary` is under the existing `/api/admin/*` security policy. Before Patroni is called, observability-api requires:

- exactly one fresh current Primary;
- the control API to be running on that current Primary;
- current Primary reconciled, unfenced and traffic-ready;
- target `can_be_primary=true`, fresh, reconciled, unfenced and hot-start-ready;
- target PostgreSQL healthy and confirmed by Patroni as a running/streaming replica;
- matching Node Agent revisions, r57 or newer;
- known Patroni replica lag within the configured limit (16 MiB by default);
- Cloudflare configured on the target when edge failover is enabled.

After Patroni accepts the switchover, the HTTP handler returns immediately. It does not attempt to wait inside the old Primary process while that process may be stopped by the role change. The browser follows the actual cluster state instead.

## Deployment

This change does **not** replace the installed cluster-control bundle. Keep A/B/C on r57 and do not run `cluster.sh migrate` for this application update.

Rebuild/update only:

- `front`
- `observability-api`
- `support-bot`

The embedded legacy `deploy/cluster` source is not the installed r57 control plane and must not be used as a replacement for it.
