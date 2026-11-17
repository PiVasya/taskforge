# Stage 5 — YugabyteDB YSQL / K3s source preparation

This stage is additive to the immutable Stage 4 source patch: no GitHub push, no production deployment, no PostgreSQL data deletion. Legacy Compose remains PostgreSQL and the original K3s stub is preserved.

**Goal:** replace PostgreSQL as the *future K3s persistence engine* with a single replicated YugabyteDB YSQL universe. RF3 uses three independent tablet storage nodes and majority commit. No app-level triple writes, no per-user SELECT fan-out, and no claim that three independent Yugabyte instances form one consistent cluster.

Added / transformed:

- 12 API `Program.cs`: `Database:RequireYugabyte` checks `SELECT version()` and a `-YB-` version marker before schema or data access. `Database:MigrationJobOnly` exits after migrations, before the web host runs. Flags are disabled by default so legacy production behavior is unchanged.
- The 12 API migration Jobs bypass the Stage 2 API-replica migration guard **only** in `Database:MigrationJobOnly` mode. Tasks backfill and cleanup run only in that maintenance job; execution Redis interactive initialization is skipped in migration jobs.
- `tools/k3s/manifest.mjs` / `generate.mjs`: generate migrations and app Deployment/Service manifests for all 12 database-owning APIs plus the rating worker and preserved Telegram quiz bot (separate migration Job). K3s app pods explicitly disable EF migrations on startup; one-shot Jobs explicitly enable migration mode. Connection strings come from existing per-service Kubernetes Secrets, not Git.
- `infrastructure/k8s/taskforge-yugabyte/db`: three-node local K3s lab with hard node anti-affinity, pinned official Yugabyte Helm chart, preflight and postflight verification, SQL bootstrap of all historic logical database names. NOT a multi-country production installer.
- `infrastructure/k8s/taskforge-yugabyte/README.md`: real multi-cluster YB Operator topology prerequisites, offline cutover, migration ownership / data integrity gates.

**Unchanged:** legacy PostgreSQL, old Docker Compose, main frontend tests/math/image/code/SQL, deterministic account analyzer, historical database contents, Stage 1–4 LLM/CT retirement decisions.

**Cutover gate:** cannot call this production-ready until all PostgreSQL-specific SQL, migrations, idempotency, backups, network+TLS, multi-K3s provider, gateway/worker/runner runtime, and live YSQL tests pass. Moving `TaskAttempts` to solutions is explicitly still pending.

**SQL sandbox boundary:** `sql-postgres`/`sql-mysql` are isolated student execution engines, not replacements for the application YugabyteDB cluster. Never execute student SQL against TaskForge application databases.
