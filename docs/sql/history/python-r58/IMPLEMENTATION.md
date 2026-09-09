# SQL task runtime implementation

Base: user-provided develop(212), not an older intermediate SQL patch. The user
created Tasks, Execution and Solutions `AddSqlDomain` migrations. Their files,
designers and snapshots are preserved byte-for-byte. No Entity/DbContext change was
made in this runtime stage. `scripts/ci/check-sql-runtime.py` checks all 92 protected
migration/snapshot files plus 104 Domain/Data files and retention of the original
source files. The authoritative limitations and test results are in QA_RUNTIME.md.

## End-to-end paths implemented

Tasks owns datasets, immutable dataset versions, engine profiles, versioned SQL
assignment specifications, dataset validation and expected-artifact receipts. SQL
catalog/edit APIs enforce the existing editor/admin policy and dataset ownership.
The learner DTO contains schema/seed/starter and supported profiles, not reference
SQL or expected artifacts. The private judge binding identifies the exact published
specification and profile. Existing Assignment.TestsJson is not used by SQL.

Publication schedules durable `sql-materialize` jobs. Each enabled target must have
a successful validation receipt and an expected artifact before publish is allowed.
The reference runs on clean disposable sandboxes twice to reject detected
nondeterminism; this is a useful check, not a mathematical determinism proof.
Dataset materialization keys and assignment-specific expected keys are distinct.
A shared dataset does not make two assignments share expected answers.

Execution owns durable jobs, capability-aware SQL claims, exact-profile routing,
worker leases, requeue and completion delivery. The existing code claim/reaper and
completion paths explicitly exclude SQL jobs. RabbitMQ fanout is only a wakeup;
fallback polling and durable jobs remain authoritative. Completion ACK retry does
not rerun successful student SQL. Terminal graded delivery is retried durably.

Solutions uses the existing SolutionSubmission, access, quota, rating-dirty and
progression path. Check pins the specification and runtime profile, uses a stable
request id, and a Preparing submission acts as a recoverable enqueue outbox. Run
has no submission, grade or task-energy charge. Technical failure refunds a charged
check under the existing quota policy. A preview never receives expected/reference
content. Existing source text storage is reused; SQL is a distinct task/job kind.

The frontend contains a dataset picker and version editor, tables/columns, portable
types, primary/foreign keys, unique indexes, defaults and seed grid. Assignment
editing supports modes, engines, reference/starter overrides, validation and publish.
The solve page has Monaco SQL, engine choice, Run/Check, result/schema/data snapshots
and standard submission history. Every Run starts from the initial dataset; this is
not a persistent SQL console. Check emits normal submit analytics, Run is separate.
Fresh graph export is version 5 with shared datasets[]. Legacy 3/4 remain accepted.
Private checks are exported only when checks are selected. Imported SQL assignments
must be validated and explicitly published on their destination runtime.

## Runtime and execution boundary

One sql-worker registers PostgreSQL, MySQL and SQLite adapters. PostgreSQL/MySQL
are long-lived separate sandbox servers. They are NOT the TaskForge/Patroni database.
Each attempt leases a disposable database and narrowly scoped account. SQLite uses
a disposable file and a restricted child process. There is no new server/container
per attempt and no rollback-based reuse of dirty state.

PostgreSQL uses an immutable template database. SQLite uses an immutable small file.
MySQL uses validated logical DDL/seed materialization replay; it does not claim to
have PostgreSQL-style template cloning. READY acquisition can make at most one cold
sandbox; pool refill runs separately. Idle READY/golden caches are bounded/evicted;
cleanup failures quarantine a sandbox rather than returning it to READY. Namespace
and marker checks restrict startup cleanup to the dedicated runtime.

Result, state and canonical-schema comparison are implemented, including nulls,
ordering/duplicates/numeric tolerance settings, selected state tables, per-engine
overrides and multi-statement scripts. Preview snapshots describe post-script state,
including safe partial-error snapshots where inspection remains possible.

The normal database profile deliberately rejects server administration, account
creation, explicit transaction control, temporary-table administration, extension
loading and unsupported advanced schema objects. CREATE/ALTER/DROP TABLE and normal
DML/index/view operations are supported only within the restricted profile. The
schema inspector fails closed for features it cannot represent safely; it does not
silently certify an arbitrary vendor-specific schema. This is not a full SQL Server,
DBA, stored-procedure, trigger, plugin or transaction-course environment.

The query child opens only its sandbox connection/file before applying seccomp,
resource limits and engine authorization. It has no TaskForge credential or Docker
socket. Network creation and new filesystem opens are then denied. SQLite authorizer
blocks ATTACH and unsafe PRAGMA/extension/file operations. Wall-clock cancellation,
statement/result/byte/connection/concurrency limits and container memory/CPU limits
bound work. Whole-engine limits do not provide per-database cgroup isolation;
co-located untrusted attempts still share an engine failure domain. This residual
risk requires the real security/load gate and capacity planning before public use.

## Profiles, failover and cache

Profiles include actual engine version, pinned engine image digest, adapter version,
settings and executor fingerprint (worker code/Python/dependencies). A different
profile cannot silently claim an old job. Keep the same image set on A/B. Upgrading
worker code, dependencies or engine images can require new spec validation and
publication; the old profile is not transparently rewritten.

Logical datasets/specs/expected receipts are business data. GOLDEN/READY/sandbox
state is local disposable cache in separate tmpfs mounts. Deleting all runtime
caches does not delete datasets or grading history; caches are reconstructed.
No SQL sandbox data is copied during Patroni/Cloudflare switching.

r58 runs SQL on the current full Primary. A/B support that role. C remains lite and
has no SQL engines; when C is Primary the ordinary site can work but SQL execution
is unavailable. Active-active A+B execution and engine sharding are future work.
SQL health is optional/degraded, not a condition for changing Primary or proving
public HTTPS. SQL startup/stop runs outside the HA/edge reconciliation thread.

## Explicitly not certified or included

See QA_RUNTIME.md for tests not executed in the editing environment. Do not turn
source inspection, SQLite checks or mocked HA tests into a claim that PostgreSQL,
MySQL, browser E2E, production .NET or live failover passed.

Full-instance/COW providers, SQL Server/MariaDB adapters, persistent editor sessions,
active-active execution, a SQL metrics dashboard and full 100-concurrent HTTP end-to-
end load certification are not part of this candidate. Normal database sandboxes
use the provider-neutral adapter contract; they do not implement those future
physical snapshot providers. The original handoff remains the acceptance checklist,
not evidence that every item has already passed.
