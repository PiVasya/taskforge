# TaskForge two-node HA

This directory implements the first production HA topology for TaskForge.

- node **A** is preferred;
- node **B** is the hot standby;
- PostgreSQL replication is **asynchronous**: a user transaction waits only for the local primary WAL flush;
- B is promoted automatically after A is unavailable and fencing succeeds; an optional provider recovery hook can power the fenced A back on after B is safely primary;
- when A returns it is rebuilt as a standby from B, catches up, then TaskForge performs an automatic controlled failback to A;
- MinIO uses two-way asynchronous bucket replication;
- Redis and RabbitMQ are local to each node;
- Cloudflare routes public traffic according to `/ha/traffic-ready` on the host HA agent.

No etcd, Patroni or Kubernetes is required for this two-node version.

## Important safety rule

Two machines cannot distinguish a dead peer from a network partition by themselves. Automatic promotion after an unreachable peer therefore requires `HA_FENCE_SCRIPT` to prove that the previous primary can no longer write. A failed ping is not fencing.

`HA_ALLOW_UNFENCED_FAILOVER=true` exists only as an explicit emergency/accept-split-brain mode and should normally stay `false`.

## Private network

A and B communicate over WireGuard:

```text
A: 10.80.0.1
B: 10.80.0.2
```

PostgreSQL and the MinIO replication endpoint bind to these WireGuard addresses. Do not expose PostgreSQL port 5432 or MinIO port 9000 to the public Internet.

## Normal state

```text
Cloudflare -> A

A: PostgreSQL PRIMARY, full TaskForge stack, traffic-ready=200
B: PostgreSQL STANDBY, storage only, traffic-ready=503

A PostgreSQL --async WAL--> B PostgreSQL
A MinIO <----async bucket replication----> B MinIO
```

The standby keeps PostgreSQL, MinIO, RabbitMQ and Redis running. User-facing APIs/workers stay stopped until promotion, so singleton/background work is not duplicated.

## Failover

If A is reachable but its application stack is unhealthy for the configured grace period, B asks A to cooperatively yield: A stops user-facing services, waits until its outgoing MinIO replication backlog is empty, checkpoints/stops PostgreSQL, B replays through the final LSN and promotes. If the MinIO backlog cannot drain within `HA_MINIO_DRAIN_TIMEOUT_SECONDS`, the handoff is aborted and A restores its active stack instead of knowingly moving traffic to a file-incomplete peer.

If A is unreachable, B waits `HA_FAILOVER_AFTER_SECONDS`, runs `HA_FENCE_SCRIPT`, and promotes only when fencing exits 0. Cloudflare then sees B `/ha/traffic-ready` become HTTP 200.

## Automatic failback

When A boots while B is primary:

1. A never starts its old writable database as authoritative.
2. A replaces its local PostgreSQL data with `pg_basebackup -R` from B.
3. A waits until streaming is healthy and lag stays below `HA_FAILBACK_MAX_LAG_BYTES`.
4. After `HA_FAILBACK_DELAY_SECONDS`, A asks B to yield. B first stops application writers and drains its MinIO replication backlog to A.
5. A replays through B's final PostgreSQL LSN and promotes.
6. B is rebuilt as a standby from A.
7. Cloudflare sees A ready again and returns traffic to A.

The failback intentionally includes a short controlled write/service interruption. It favors data correctness over a risky dual-primary handoff.

## First installation

Russian command-by-command checklist: `QUICK_START_RU.md`.

Read `FIRST_INSTALL.md`. In short:

1. Prepare the same standalone server bundle on A and B.
2. Configure WireGuard on both nodes.
3. Initialize A with `setup-primary.sh`.
4. Copy the generated peer `.env` to B and initialize B with `setup-standby.sh --yes`.
5. Configure MinIO replication.
6. Configure the optional SSH-over-WireGuard state sync for TLS/DataProtection volumes.
7. Configure a real fencing hook and, for fully automatic A recovery, a provider recovery/power-on hook.
8. Configure Cloudflare Load Balancing to monitor port 9187 `/ha/traffic-ready`.
9. Run `preflight.sh` on both nodes.
10. Run `enable-auto-failover.sh` on both nodes. Bootstrap scripts intentionally keep automatic promotion disabled until this point.
11. Test failover and failback before treating B as production HA.

## Useful commands

```bash
sudo ./ha/status.sh
sudo ./ha/preflight.sh
sudo systemctl status taskforge-ha
sudo journalctl -u taskforge-ha -f
sudo wg show wg-taskforge
```

The public/read-only agent endpoints are:

```text
GET :9187/ha/live
GET :9187/ha/traffic-ready
```

Peer control endpoints live under `/v1/control/*`; they require the shared bearer key **and** a request originating from the configured peer WireGuard IP.

## What is and is not replicated

Replicated:

- every PostgreSQL database in the PostgreSQL cluster via WAL;
- TaskForge file bucket through MinIO bucket replication; cooperative failover/failback waits for the active source backlog to reach zero before PostgreSQL handoff;
- TLS certificate and ASP.NET DataProtection Docker volumes through the optional active-to-standby SSH sync;
- deploy secrets are initially copied from A to B with `make-peer-env.sh`.

Not replicated by design:

- Redis cache;
- RabbitMQ queue contents;
- container runtime/cache/logs.

TaskForge's durable execution state lives in PostgreSQL; local RabbitMQ is treated as transport. After failover, stale/interrupted work is recovered by the application/watchdog paths rather than stretching one RabbitMQ cluster across the WAN.

## Updating HA nodes

Use the normal standalone `./update.sh` on each node. In HA mode it pulls images and calls `agent.py apply-update`:

- on the active primary it recreates the active stack and restores readiness only after gateway health succeeds;
- on the standby it does **not** start the user-facing stack.

A standby also periodically prefetches images through `taskforge-ha-prefetch.timer`.

Operational recovery: `RECOVERY.md`.
