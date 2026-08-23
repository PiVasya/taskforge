# TaskForge N-node data sync and failover

TaskForge production supports three or more full nodes from one shared topology file.
The preferred initial layout is A/B/C with three etcd voters and quorum 2/3.

- WireGuard provides an encrypted full-mesh network.
- Patroni and etcd guarantee a single writable PostgreSQL primary.
- PostgreSQL streaming replication is asynchronous. A successful user transaction
  waits for the local primary WAL flush, not for remote replicas.
- MinIO replicates application objects asynchronously between all configured nodes.
- Redis and RabbitMQ are local per node; PostgreSQL remains the durable source of
  truth for recoverable execution state.
- The host-level `deploy/cluster/agent.py` starts the application stack only on the
  current Patroni primary and maintains `.runtime/cluster/readiness/traffic-ready` for the gateway.
- Cloudflare routes traffic only to origins returning HTTP 200 from
  `/ha/traffic-ready`.
- The preferred primary is selected by priority when safe. When A returns after a
  failover, it rejoins as a replica, catches up, and only then receives a controlled
  Patroni switchover if automatic failback is enabled.

A sudden primary failure can lose the newest asynchronous WAL or object writes that
had not reached another node yet. This is the deliberate latency/durability tradeoff
chosen for TaskForge.

For 3–5 full TaskForge nodes, keep A/B/C as the three etcd voters and add D/E as
non-voter Patroni replicas. Do not change live etcd membership by only editing JSON.

Deployment instructions: `deploy/cluster/QUICK_START_RU.md`.
