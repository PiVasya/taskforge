# TASKFORGE 107 — Primary switch live progress

Base: `taskforge-develop(208)`.

## Problem

The Primary switch endpoint intentionally returns immediately after Patroni accepts the switchover. During a real role change the old Primary may stop the application stack before the HTTP response reaches the browser. The previous UI correctly refused to retry the mutation, but then reduced the whole operation to `Primary A / Traffic WAIT`, which made a safe uncertain response look like a silent hang.

## Update

`/admin/cluster` now derives a live switchover pipeline from the existing r57 Node Agent telemetry. No new control-plane protocol is required.

The progress card exposes these independent stages:

1. request receipt;
2. observed Patroni/Agent Primary role;
3. application activation on the target;
4. Cloudflare DNS target;
5. public route confirmations;
6. HTTPS/TLS proof;
7. final `traffic_ready`.

It also shows the current observed Primary, target Agent role, current edge phase, route confirmation counter, final traffic state, target telemetry time, edge `last_error`, and retry countdown.

If the POST response is lost and fresh telemetry keeps showing the old Primary plus the requested target as a standby for at least 20 seconds, the UI explicitly says that the role change is not being observed. It never retries the POST automatically. The operator may refresh the observation immediately or clear only the browser's local pending marker after that factual no-transition state is established.

When the target becomes Primary, a lost HTTP response is automatically resolved by fact: the request stage changes to `confirmed by observation`, and the card continues through application/DNS/route/TLS/traffic stages.

## Deployment

This is an application update only. Keep the installed cluster-control bundle on r57.

Rebuild/update:

- `front`

The observability API and Node Agents already expose all telemetry fields used by this progress view.
