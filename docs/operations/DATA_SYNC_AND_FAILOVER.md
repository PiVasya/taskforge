# TaskForge data sync and two-node failover

Production HA for the first two servers is intentionally active/passive.

```text
Cloudflare
   |
   +--> A (preferred active)
   +--> B (standby)

A <---- WireGuard ----> B
```

The current implementation is in `deploy/ha/` and is mirrored into the standalone server bundle under `ha/`.

## PostgreSQL

PostgreSQL is the durable source of truth. The active node is writable and streams WAL asynchronously to the standby.

```text
A PRIMARY --async WAL--> B STANDBY
```

TaskForge does not wait for B during a normal transaction. `synchronous_commit=on` keeps the normal local WAL durability guarantee on the active PostgreSQL, while `synchronous_standby_names` is explicitly empty so remote WAN latency is not on the commit path.

No permanent replication slot is used. This is deliberate: a dead standby must not fill the primary disk with retained WAL. `wal_keep_size` provides a bounded catch-up window. If a reachable standby loses WAL streaming long enough, the HA agent automatically takes a fresh `pg_basebackup` from the current primary.

## Failover

A reachable-but-unhealthy active node can cooperatively yield to its standby. For a completely unreachable peer, automatic promotion is blocked until the configured provider fencing hook confirms that the old primary cannot write. After successful promotion, an optional provider recovery hook can power the fenced machine back on; it then rejoins as a standby. This separates the safety-critical OFF decision from the later recovery/power-on action.

The system does not auto-resolve a detected dual-primary condition. Both nodes remove traffic readiness and require operator recovery. Choosing A merely because it is preferred can lose writes from B's timeline.

## Automatic failback

A is preferred. If B was promoted and A later returns, A is rebuilt from B as a standby. After it is streaming and stably caught up, B performs a controlled yield and A promotes. B then rejoins from A.

## MinIO

The current TaskForge deployment uses one MinIO server per host, not a distributed MinIO deployment. The HA scripts therefore use two-way asynchronous **bucket replication**, not MinIO site replication.

Existing A objects are seeded to an empty B bucket before versioning/replication is enabled. Future writes/deletes replicate both ways. For a cooperative failover or automatic failback, the active node first stops TaskForge application writers and waits for its outgoing MinIO replication backlog to become empty before yielding PostgreSQL. This prevents a planned handoff from moving traffic to a peer that is still missing known file operations. A hard host failure cannot perform this drain and therefore retains the normal asynchronous-replication loss window for the newest files.

## RabbitMQ and Redis

RabbitMQ is local transport per node; it is not stretched as a WAN cluster. Durable job state is retained in PostgreSQL and existing recovery/watchdog paths handle interrupted execution work after promotion.

Redis is a local cache and is intentionally not replicated.

## TLS and ASP.NET key rings

`letsencrypt`, `content-data-protection`, and `quiz-data-protection` Docker volumes are copied from the active node to the standby over SSH through WireGuard by `taskforge-ha-state-sync.timer`. This keeps the standby ready to terminate TLS and use the same framework key material.

## Public routing

Cloudflare monitors the host HA agent on TCP 9187 path `/ha/traffic-ready`. Only the current active node returns 200. Standby returns 503. This separates public traffic selection from PostgreSQL role election/promotion logic.

See:

- `deploy/ha/FIRST_INSTALL.md`
- `deploy/ha/FENCING.md`
- `deploy/ha/CLOUDFLARE.md`
