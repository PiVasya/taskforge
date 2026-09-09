# SQL execution on Go

## Scope and compatibility

The production SQL coordinator and short-lived execution helper are Go. The source
is services/execution/sql-worker. No Python interpreter, Python SQL driver, pip
package or Python child is used in this service. The C# Tasks/Solutions/Execution
APIs, shared contracts, frontend and user-generated migrations are retained from
the previous complete SQL candidate. SQL remains its own sql-test type, with
reusable datasets and immutable published specifications; it is not TestsJson.

This is Go with a small CGO boundary, not a pure-Go/static executable. Database
protocol handling uses the native libpq and MariaDB Connector/C client libraries;
SQLite uses its native library. The maintained database clients perform actual
wire protocol/authentication work. Go owns jobs, adapters, pool state, verification,
limits, lifecycle, metrics and cancellation. The repository has no external Go
module dependency for this worker. The Docker runtime still needs shared libraries.

C# wire contract version 1 and adapter contract 1.0.0 are preserved. Implementation
identity is go-native-v1. The runtime fingerprint includes the built executable,
Go/runtime/native-library versions, engine settings and pinned engine image. Old
Python profiles are NOT relabeled as Go profiles. SQL jobs are claimed only with
the exact registered profile. See DEPLOYMENT.md before replacing a running r58.

## Build and artifacts

Docker uses golang:1.26.8-bookworm for compilation and debian:bookworm-slim for
runtime. Build dependencies: gcc, pkg-config, libpq-dev, libmariadb-dev,
libsqlite3-dev. Runtime packages: ca-certificates, libpq5, libmariadb3, libsqlite3-0.
The image runs UID/GID 10001:10001 and has no Python, Go compiler or build toolchain.
The explicit integration Docker target contains precompiled test binaries; the
final release target does not. All images retain the project's debug flags at 1.

Local checks require Linux, Go, a C compiler and pkg-config discovery of libpq,
libmariadb and sqlite3. From the source root:

```bash
bash ./scripts/check-sql-go.sh
```

This executes formatting checks, go vet, race-enabled tests, real isolated SQLite
runs, the 100 Run + 100 Check test, and a release-helper build/isolation probe. It
never starts the business APIs or writes migrations. The local Go toolchain must
be available already; GOTOOLCHAIN=local prevents an implicit network download.

The portable module syntax floor is Go 1.23; it is not the production image pin.
QA_RUNTIME.md states which compiler and native-library versions were actually
executed in this editing environment. The exact production compiler/base image and
real servers are verified by scripts/sql/test-engines.sh, not by assumptions about
local compatibility.

## Execution flow

1. C# execution-api retains the durable job. RabbitMQ is a wakeup only.
2. The coordinator registers exact profiles and claims compatible jobs under a
   bounded worker slot and lease. Poll fallback is 500 ms; concurrency defaults to 2.
3. The provider leases a READY sandbox or performs at most one foreground create.
4. The coordinator starts the same executable in child mode for this execution.
   PostgreSQL/MySQL servers are NOT restarted and no attempt container is created.
5. The child executes the script, collects bounded post-script result/schema/data,
   and exits. The parent verifies against the expected artifact for Check.
6. The provider destroys the used sandbox. Cleanup failures quarantine it and fail
   closed. The sandbox is never returned to READY with student changes.
7. Completion retries use the same job/lease and do not rerun SQL after an ACK loss.
   C# continues to own result delivery, normal submissions, rating and progression.

Reference SQL runs only during validation/materialization, twice on independent
sandboxes to reject detected nondeterminism. This is a practical test, not a proof
of determinism. Run has no graded submission or energy/progress side effect. Each
Run starts from the initial dataset; it is not a persistent editor session.

## Engines and providers

PostgreSQL: immutable template database, disposable cloned database and dedicated
restricted account. MySQL: validated logical DDL/seed artifact, fresh small database
and restricted account prepared in the READY pool. SQLite: immutable file, private
small-file copy per sandbox. A physical PostgreSQL cache is never used as a MySQL
cache. Shared datasets deduplicate materialization, not assignment expected answers.

The provider interface is independent of the engine. Reflink/COW/full-instance
providers, SQL Server, engine sharding and active-active execution are not added by
this port. Ready/max-materialization/idle bounds remain explicit. Cold creation is
singleflight per materialization; no synchronous full-pool refill is on the user
path. Reset/retirement fences wait for active leases before destroying old caches.

## Child security and resource boundaries

The helper receives only its sandbox configuration, SQL, profile and limits. It
never receives the TaskForge internal key, Rabbit credentials, admin DB credentials
or expected answers. It opens the one permitted DB connection/file first, validates
the engine, then seals all existing OS threads with seccomp TSYNC. A thread-local
seccomp call alone would not constrain Go/native driver threads.

The filter denies new file opens, sockets/connections, process creation/exec,
ptrace, mount and other unrelated operations. It permits only the constrained
thread creation that Go needs inside the same process; clone3 returns ENOSYS to
force inspectable legacy clone flags. Runtime signals are restricted to the same
process. The startup probe checks restrictions on multiple existing OS threads.
Unsupported isolation fails closed, never falls back to unsandboxed execution.

Engine grants are a separate security boundary. The marker of the dedicated SQL
engine is checked before every admin operation. Cleanup addresses only the worker's
namespace. SQLite uses native authorizer/progress callbacks; ATTACH, extension
loading and unsafe PRAGMA are denied. New SQL servers are on separate private
Docker networks, have no public ports, business DB data, host paths or Docker socket.

Parent: bounded slots and Go soft heap limit 128 MiB by default in Compose.
Child: GOMAXPROCS=1, Go soft heap limit 64 MiB, at most 16 OS threads, no core dump,
NOFILE=64, file-size/CPU limits, parent-monitored RSS+swap ceiling 256 MiB and a
wall-clock kill. Output is capped; native PG/MySQL row retrieval is streamed/bounded.
The parent kills the whole child process group and cleans the sandbox after timeout.
There is no RLIMIT_AS because Go reserves virtual address ranges. Soft Go heap limits
are not total RSS limits; native allocations are also subject to the parent monitor
and the container memory limit. The worker cgroup remains the hard shared ceiling.

This does not supply a per-database cgroup within a shared server. Two attempts can
still contend for a server's CPU/RAM/I/O. Whole-engine limits, statement/time caps,
connection limits and low concurrency bound exposure but do not eliminate that
failure domain. Actual PostgreSQL/MySQL hostile-query tests remain a mandatory gate.
ARM64 filter code exists but was not executed here; Linux/amd64 was exercised.

## Control plane and wakeup

The existing C# HTTP job/lease APIs remain authoritative. Requests are size-bounded,
do not follow redirects or use environment proxy credentials, and do not log secret
response bodies. A separate lease watchdog cancels a child before its claim can
become unowned; completion transport retry never restarts the query.

The Go wakeup component implements a bounded AMQP 0-9-1 consumer subset using only
the standard library: PLAIN negotiation, fanout exchange, exclusive auto-delete
queue, auto-ack delivery, heartbeats and reconnect. It is not a general AMQP client
and is not a durable job broker. Malformed frames fail the connection. Broker loss
falls back to durable polling. Unit tests drive a fake TCP broker; a real RabbitMQ
management/AMQP integration test is included in the Docker gate. No extra RabbitMQ
container is added to production; the additional Rabbit container is test-only.

## Observability

/health, /ready and /metrics use internal port 8081. Healthcheck executes the native
binary, not Python. Pool ready/leased/replenishing/cold-create, materialization,
query, verification, cleanup, duration, timeout and result-limit metrics are retained.
Go adds helper inflight, elapsed/CPU duration and peak RSS measurements.

sql_engine_connections{engine,scope="coordinator"} counts native handles in the
coordinator process only. It is NOT a server-wide connection gauge and does not
silently include handles in child processes. Helper inflight is reported separately.
Do not infer a load capacity or a speedup versus Python from these counters alone.

## Production boundary

r59 is a complete independent package. A/B remain full; C remains lite. Only the
current full Primary runs SQL in this candidate. SQL failures are auxiliary/degraded,
not Primary/HTTPS readiness conditions. Cache transfer is not part of failover.
No old OJ runner, business Entity or migration is changed by the Go port.

Python remains in the repository's pre-existing cluster/control and CI utilities,
and in the supported Python programming-language runner. Those are not this SQL
service. This change is not a rewrite of the whole TaskForge project in Go.
