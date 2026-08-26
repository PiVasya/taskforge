# TaskForge 105 — Cluster HA / observability

This source update is paired with production bundle v40.

## Added

- `/admin/cluster` is the TaskForge cluster control/observability page; legacy `/admin/system-status` redirects there.
- Server-level React Flow map for A/B/C (HA/PostgreSQL/MinIO/WireGuard layers).
- Per-node host, container, readiness and update details.
- Image matrix compares versions without exposing image ids/digests.
- `observability-api` aggregates host Node Agent telemetry from all nodes.
- `support-bot` accepts secured internal cluster events and can announce updates,
  failovers and cluster errors.
- Gateway browser upstream and tasks image-analyzer upstream are configurable so
  a lite C node can delegate heavy functions to active B.

## Privacy / security boundary

Immutable Docker image fingerprints exist only inside Node Agent telemetry and
inside the observability comparison process. They are removed from the admin API
response. The browser only receives comparison states:

- `same`
- `different`
- `missing`
- `unknown`
- `not-assigned`

The Node Agent endpoint is bound to the WireGuard address and restricted by the
server firewall.

## Event delivery

Node Agent keeps a short host event history. Observability aggregates it and
forwards selected events to support-bot. Failed bot delivery is retried; the bot
de-duplicates by event id so an update notification is not lost during a
Watchtower restart.

## v40 revision 2 hardening

The final v40 model keeps Watchtower running on all three nodes in steady state. B updates the complete stopped FULL profile; C updates only its stopped LITE profile and never creates/pulls excluded browser/image services. `revive-stopped` stays disabled. Node Agent pauses the updater only for short role/container transitions and restores it after the application reaches readiness.

The follow-up audit also fixed non-disruptive quorum preparation, dynamic post-failover primary checks, safe standby deploy/update commands, source-tree compose path handling and a duplicate Patroni `tags` mapping that could otherwise overwrite C's `nofailover` protection.

The admin API still compares immutable internal image fingerprints, but the browser receives only human states such as `Одинаковая версия`, `Версия отличается`, `Обновляется`, `Не скачан` and `Не назначен`. Raw image IDs/digests and raw updater error payloads are not exposed.
