# TaskForge Cluster Manager v32

v32 is a diagnostics hardening release based on the first successful real A/B adoption.

## Fixed from real deployment

- PostgreSQL standby receiver checks no longer use nested `\047` SQL quoting that silently failed under `sh -lc`.
- `status` reports primary replica state/async lag and standby receiver source/slot/replay gap.
- `doctor` is exhaustive: a failed probe does not abort later probes.
- `doctor` checks WireGuard peer count, public data-port exposure, UFW service, PostgreSQL role/streaming/lag, MinIO health and CPU image, RabbitMQ, Redis, bucket versioning, and MinIO replication rules when `mc` is installed.
- `adopt` installs the local `mc` helper and ensures local bucket versioning without recreating PostgreSQL/MinIO containers or volumes.

Validated against the observed production topology: A primary, B async streaming standby with zero-byte lag, and bidirectional MinIO bucket replication.
