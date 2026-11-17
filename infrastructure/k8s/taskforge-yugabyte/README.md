# TaskForge Stage 5 — K3s + YugabyteDB YSQL

Stage 5 is a **cumulative source patch on top of Stage 4**; apply it to the exact clean develop commit noted in READ_FIRST.txt. Do not apply Stage 4 separately.

## Invariant: replication is the database's responsibility

A successful business write is a **single YSQL transaction**. YugabyteDB distributes its writes to an RF=3 Raft group (majority 2 of 3). We do **not** send three independent INSERTs from HTTP handlers. A read-only click has nothing to persist; not every click is a write. This design provides consistency inside one Yugabyte universe. Three separate Yugabyte clusters do *not* become one universe automatically.

This directory provides:

- `db/helm-values-lab.yaml`: a three-node, **single-K3s-cluster lab**, with distinct-node affinity, persistent local disks and no public load balancer.
- `db/install-lab.sh`, `db/verify-lab.sh`, `db/create-lab-databases.sh`: explicit lab setup and checks; never safe to treat as a production deployment.
- `tools/k3s/generate.mjs` at the repository root: 12 API deployments/services, rating worker and independent Telegram bot deployments, plus 13 one-shot EF migration jobs, using existing K8s Secrets.
- Program.cs changes in 12 API projects: opt-in Yugabyte engine guard and `Database:MigrationJobOnly` mode. The rating worker and Telegram quiz bot also reject a wrong SQL engine in K3s. Legacy Compose is untouched unless these flags are set.

**Not included:** cluster networking, Cross-Cluster Kubernetes DNS, K3s install, YugabyteDB Anywhere/Operator installation, external HTTPS gateway, Redis/RabbitMQ/MinIO, execution worker/runner deployment, production credentials or TLS provisioning. Without those, a complete public TaskForge production rollout is not ready.

## Lab on 3 independent physical K3s nodes

Kubernetes node labels are intentional so RF=3 is not mistaken for 3 pods on one server. Three k3s virtual nodes on one physical host are not a fault-tolerant physical deployment.

```bash
kubectl label node NODE_A taskforge.io/db-node=true
kubectl label node NODE_B taskforge.io/db-node=true
kubectl label node NODE_C taskforge.io/db-node=true
TASKFORGE_YUGABYTE_LAB_ACK=yes-lab-only bash infrastructure/k8s/taskforge-yugabyte/db/install-lab.sh
TASKFORGE_YUGABYTE_LAB_ACK=yes-lab-only bash infrastructure/k8s/taskforge-yugabyte/db/create-lab-databases.sh
```

The scripts fail closed unless there are three ready, labeled schedulable Kubernetes nodes. The post-install checks require master and tserver pods on three distinct hosts, a YugabyteDB universe RF3 configuration and `SELECT COUNT(*) FROM yb_servers()` = 3. Chart version is pinned to **2026.1.3**; evaluate its exact compatibility and security posture before production. Kubernetes `local-path` disks and default unencrypted lab YSQL are **not** production-ready.

## Real topology: separate K3s clusters per country

Keep `rb`, `poland`, `israel` as separate Kubernetes clusters. Build a **single YugabyteDB universe** spanning them with one data node per region (or more), RF3, and a YugabyteDB Anywhere/Kubernetes Operator `YBProvider` whose zones have separate kubeconfig Secrets. Do not blindly deploy a separate 3-replica database in each country; that would produce **three independent sets of data**, not automatically replicated writes.

A **non-deployable placement template** is provided at `db/geo-universe.template.yaml`. It expresses RF3 with one data node in Poland, RB and Israel but deliberately omits the YBProvider and credential configuration. DO NOT apply this as-is.

All clusters require private full-mesh pod/service connectivity (for example WireGuard + correctly routed non-overlapping pod/service CIDRs), globally resolvable per-pod DNS/MCS or Istio, firewall rules for Yugabyte internode traffic, stable clocks, independent failure domains, TLS, authenticated users, PVCs designed for failures, backups and restore drills. The YBA Operator must be tested against the exact K3s version and node networking. This is an additional prerequisite, not something install-lab.sh configures.

Docs:
- https://docs.yugabyte.com/stable/deploy/kubernetes/multi-cluster/
- https://docs.yugabyte.com/stable/yugabyte-platform/anywhere-automation/yb-kubernetes-operator/
- https://docs.yugabyte.com/stable/yugabyte-platform/prepare/networking-kubernetes/

RF3 means one copy of a tablet on each of three eligible data nodes when all tablets are correctly placed and balanced. The acknowledged commit needs quorum. It does **not** mean that the .NET program sends N database requests or that reads should be fanned out to all database replicas.

## Generate K3s core API manifests (no cluster actions)

From the complete patched repository:

```bash
node tools/k3s/generate.mjs \
  --image-prefix ghcr.io/pivasya/taskforge-multinode \
  --image-tag sha-REPLACE_WITH_PUBLISHED_IMMUTABLE_BUILD_TAG \
  --release stage5a \
  --namespace taskforge \
  --replicas 2 \
  --out-dir /tmp/taskforge-k3s-generated
```

Generated:

- `01-db-migrations.yaml`: one Kubernetes Job for each of 12 DB-owning APIs, plus a separate migration Job for the preserved Telegram quiz bot. They run the existing EF Core migrations and exit **without listening on HTTP**, without hosted worker loops. Do NOT run Jobs in parallel with any other migrator touching the same logical DB.
- `02-api-deployments.yaml`: two-replica Deployments and ClusterIP Services for 12 APIs, plus single-replica Deployments for `rating-worker` and the independent Telegram quiz bot. The workers do not get HTTP Services. All core APIs set `Database__RequireYugabyte=true`, `Database__MigrateOnStartup=false`, `Database__MigrationJobOnly=false`, and `Cluster__MultiNode=true`.
- `database-ownership.json`: the exact current DB-to-service mapping. Historical `taskforge_telegram_quiz` is preserved. `content-api` correctly uses `ConnectionStrings__LearningConnection`.

Expected existing objects in namespace `taskforge`:

- Secret **taskforge-db-connections** with 14 keys named exactly after each API or worker image/service name. Each value is a complete Npgsql connection string for its **respective logical YugabyteDB database**, with host, YSQL port 5433, dedicated role/password, `SSL Mode=VerifyFull` and valid CA path for production.
- ConfigMap **taskforge-app-config** with the non-secret settings and other service URLs.
- Secret **taskforge-app-secrets** with internal service keys, Redis/RabbitMQ auth, JWT and required application secrets.
- Optional registry pull Secret **ghcr-pull** for private GHCR images. You must also arrange TLS trust bundles and working K3s service discovery.

`ConnectionStrings__LearningConnection` is used only for `content-api`. All other APIs and the rating worker use `ConnectionStrings__DefaultConnection`. Telegram quiz bot uses `ConnectionStrings__TelegramQuizDb` and disables its automatic startup migrations. This prevents an accidental write to an unconfigured implicit local PostgreSQL.

Before applying workloads, bring up Redis, RabbitMQ, MinIO, gateways, dependency APIs, execution workers/runners, ingress and Secrets. Prevent duplicated external Telegram polling and stop the old bot before enabling the new replica. These generated Deployments are a baseline, **not** a complete application rollout. Some image-specific runtime settings/resources still need tuning. The TCP probe is only startup readiness, not an application-specific health gate.

Then, deliberately, in an isolated K3s environment with correct config:

```bash
kubectl -n taskforge apply -f /tmp/taskforge-k3s-generated/01-db-migrations.yaml
kubectl -n taskforge get jobs
# Verify every migration Job succeeded; inspect logs if any failed.
kubectl -n taskforge apply -f /tmp/taskforge-k3s-generated/02-api-deployments.yaml
```

**Do not run this against a live destination containing a Voyager-imported schema without reconciling `__EFMigrationsHistory`.** Decide one owner of schema creation: either pre-approved EF migrations for a fresh K3s DB, OR Voyager's import + a carefully reviewed migration baseline. Running both will create conflicts. `Database__RequireYugabyte=true` rejects the *legacy PostgreSQL* connection, but does not by itself guarantee the YSQL schema is correct.

## Source PostgreSQL to YSQL (small DB, intentional maintenance window)

Because data is modest, use an **offline** migration with a write freeze, not mandatory CDC/dual-writes. The existing production PostgreSQL is not changed by Stage 5. The safe sequence is:

1. Back up **each** logical PostgreSQL database, verify recovery, save matching MinIO object backups and migration history.
2. Block writes at DB/application boundary, including API, background workers, bots, queues, and any old container that can still write. Snapshot or settle in-flight execution work.
3. For each database run YugabyteDB Voyager assessment, `export schema`, `analyze-schema`, **manually review** incompatibilities. Change source-to-target owner mappings where the new K3s architecture differs.
4. Import compatible schema once, import data, then run versioned TaskForge-specific data transformations as needed. Do not regenerate IDs, lose completed math/test attempts, merge historical user tables, or silently drop JSONB.
5. Validate row counts, user/assignment relationships, solution IDs, grades, imports, leaderboard, permissions, and checksum of MinIO object content. Verify database quorum and final app smoke tests before accepting writes.
6. Redirect traffic and release the write fence. Retain immutable old PostgreSQL backup for recovery. After the new system accepts writes, simply pointing DNS back to PostgreSQL is **not** a safe rollback.

Voyager can migrate data between PostgreSQL and YSQL, but cannot automatically rewrite arbitrary TaskForge business ownership models. For a 5–10 minute copy target, perform a timed full rehearsal before promising the maintenance duration.

See https://docs.yugabyte.com/stable/yugabyte-voyager/migrate/ and https://docs.yugabyte.com/stable/yugabyte-voyager/migrate/offline-migrate/ .

## Existing code and unresolved work

- `tasks-api` currently owns `TaskAttempts` (math/test) despite legacy splitter docs assigning attempts to solutions. Stage 5 does **not** fake a completed historical migration; moving that table and all endpoints to `solutions-api` is a separate coordinated app/data change.
- `execution-api` job queue remains a separate logical DB; do not replay old pending executions blindly on cutover.
- `services/shared/Import/PostgresImportLock` and use sites call PostgreSQL-specific advisory-lock functions. Yugabyte YSQL support is version-specific; run integration tests with YugabyteDB **2026.1.3** and validate timeout/retry/lease behavior.
- `rating-worker` reads `taskforge_solutions` and calls `tasks-api` for test/math activity; relocating attempts without changing rating is invalid.
- Other services, CI and migrations need real YSQL build/integration tests. Stage 5 does not claim a completed cutover.

**Never run K3s against live legacy PostgreSQL with migration-on-start enabled.**

### Distinguish application storage from untrusted SQL sandboxes

The standalone `sql-postgres` and `sql-mysql` containers execute **untrusted student SQL** under the existing SQL runner isolation model. They are not the TaskForge application datastore. Do not point them at YugabyteDB, reuse application DB credentials, or allow SQL submissions to connect to application storage. YugabyteDB replaces only the K3s **application** PostgreSQL databases; these isolated execution engines still need secure K3s runner deployment.
