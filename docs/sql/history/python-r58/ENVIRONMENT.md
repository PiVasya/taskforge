# SQL environment and resource defaults

Do not paste production business DB credentials into any SQL_* variable. These
engines contain disposable educational data only. Do not expose their DB ports.

## Locally generated and preserved

`scripts/sql/prepare-runtime.py` writes a mode-0600 local environment file atomically.
Dev uses .runtime/sql-runtime.env; standalone r58 uses its stable config area's
sql-runtime.env. The script generates different local engine passwords and namespace
marker, resolves registry RepoDigests and preserves existing pins. It never creates
business DBs, migrations or volumes. The worker refuses an unprepared marker/profile.

| Variable | Meaning |
| --- | --- |
| SQL_ENABLED | true on full nodes unless explicitly disabled; false for C/lite |
| SQL_WORKER_ID | unique sql-A / sql-B / local worker identity |
| SQL_SANDBOX_MARKER | generated 64-hex runtime-only namespace authorization marker |
| SQL_POSTGRES_PASSWORD / SQL_MYSQL_PASSWORD | generated runtime-admin credentials; parent worker only |
| SQL_POSTGRES_IMAGE / SQL_MYSQL_IMAGE | saved registry image@sha256 pins; initial defaults postgres:18-bookworm / mysql:8.4 |
| SQL_POSTGRES_RUNTIME_DIGEST / SQL_MYSQL_RUNTIME_DIGEST | matching sha256 fingerprints for registered profiles |
| TASKFORGE_SQL_INIT_ROOT | wrapper-resolved init-script directory, not a user absolute project path |

Preparation on another node must use the SAME PostgreSQL/MySQL image@sha256 pins
and the SAME built sql-worker image. Share image pins, never node passwords/marker.
Do not refresh floating tags independently on A and B. Existing pins are not replaced
by a normal migrate. Intentional engine upgrades need controlled revalidation.

## Configurable resource settings in the normal environment

| Variable | Default |
| --- | --- |
| SQL_CONCURRENCY | 2 per worker (validated range 1..8) |
| SQL_POOL_MAX_READY | 6 total READY/replenishing slots per worker |
| SQL_POOL_MAX_MATERIALIZATIONS | 4 per engine |
| SQL_POSTGRES_MEM_LIMIT / SQL_MYSQL_MEM_LIMIT / SQL_WORKER_MEM_LIMIT | 768m / 1024m / 768m |
| SQL_POSTGRES_CPUS / SQL_MYSQL_CPUS / SQL_WORKER_CPUS | 0.75 / 0.75 / 0.75 |

Memory limits include the containers' tmpfs pages; tmpfs is not free disk. The
combined memory ceilings are about 2.5 GiB, on top of the existing application and
business-storage needs. Ready counts are small on purpose. Raising concurrency
without measuring memory/temporary-space use is not a safe scaling strategy.
Engine data tmpfs is 384m PostgreSQL and 512m MySQL; worker cache is 96m. Temp paths
are separately bounded. CPU shares are 128 and OOM adjustment is 700 for auxiliary
SQL containers; this does not guarantee immunity for the rest of the host.

Worker standalone options also include SQL_CACHE_DIR, SQL_POOL_IDLE_READY_SECONDS
(default 120), SQL_POOL_IDLE_GOLDEN_SECONDS (600), per-engine HOST/PORT/USER and API
URLs. Compose supplies private-network hosts and existing internal API/RabbitMQ
configuration. Do not set an engine host to the business PostgreSQL/Patroni endpoint.

API Rabbit wakeup uses Sql__RabbitManagementUrl/User/Password with the existing
RabbitMQ management listener. Worker AMQP uses RABBITMQ_HOST/USER/PASSWORD. Missing
wakeups fall back to durable polling; missing business/internal authentication does
not bypass access checks. TASKFORGE_DEBUG_LOGS and build debug flags remain 1.

## Task-level limits

Defaults: 5-second wall timeout (hard maximum 10), 200 preview rows, 1000 result rows,
1 MiB result payload (hard maximum 2 MiB), 20 statements (hard maximum 50), 100000
source characters. Dataset definitions are bounded to 2 MiB, 24 tables, 64 columns
per table, 1000 rows per table and 5000 rows in total. These are implementation
ceilings, not load-tested production capacity promises. Adjust task settings only
within the contract's enforced limits.

Metrics are exposed on the internal worker port 8081 at /metrics. /health indicates
process liveness; /ready exposes profile readiness. No SQL DB/metrics host ports are
published by default. r58 status/doctor reports optional SQL health without printing
secrets. A dedicated graphical metrics dashboard is not included.
