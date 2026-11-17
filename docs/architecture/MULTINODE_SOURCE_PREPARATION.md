# TaskForge multi-node application source preparation

This document covers the **application repository and its build**, not node installation.
No machine names, static IPs, regions, node role selections, or K3s manifests are part of these sources.
The independent deployment repository will own node topology, traffic ingress and storage.

## Source branch and image isolation

The original `docker images` GitHub Actions workflow now accepts pushes to any source branch.
`develop` and `main` keep the existing images at `ghcr.io/<owner>/<repo>/<service>:develop|main`.
Other branches publish to `ghcr.io/<owner>/<repo>-multinode/<service>:<normalized-branch-name>` by default.
The optional GitHub Actions repository variable `TASKFORGE_MULTINODE_IMAGE_PREFIX` overrides the
`ghcr.io/<owner>/<repo>-multinode` prefix; it must include `ghcr.io/`.

Branch names only determine build identity. A branch cannot imply a physical node, region,
service replica count, or cluster Primary.

The first push to a new source branch automatically runs a full image build because
its separate GHCR path has no earlier successful baseline. Later pushes use the change matrix.
For a manual full rebuild select `build_all=true` and **explicitly** `publish_images=true`.
A manual build does not publish by default; production branch dispatches retain build-only defaults.
Do not point production Watchtower or Compose at the multi-node image prefix until an independent
runtime compatibility and migration plan has been completed.

## Runtime behavior prepared here

### Shared interactive compiler tickets

`execution-api` now supports two modes:

- `InteractiveSessions__Store=memory` (default for current single-instance Compose).
- `InteractiveSessions__Store=redis` (required in a multi-node cluster).

`Cluster__MultiNode=true` automatically selects Redis and refuses a memory override.
Configure a reachable Redis connection via the already-supported `ConnectionStrings__Redis`,
`Redis__ConnectionString`, or `Cache__RedisConnection` (and leave `Cache__Enabled=true`).
The existing TaskForge cache registration provides the Redis connection multiplexer.

Short-lived tickets are stored with a 75-second TTL, one-time activation is atomic, and a
per-user sorted-set lease enforces the current 1-session / 3-admin-session limits across
all replicas. A running session retains its lease for 3 minutes, with early release on exit.
Redis failures are not converted into node-local tickets (unsafe when load-balanced).
Lua operations use one fixed Redis Cluster hash slot (`{compiler}`) so all script keys are
legal even with a sharded Redis connection. The actual console streaming still runs directly
through the API replica holding the established WebSocket connection.

Runtime admission must ensure Redis accepts scripts and is reachable before enabling replicas.
Session payloads temporarily contain the submitted source code; protect Redis access and do not
expose its port publicly. Do not mix old and new execution-api replicas in one load-balanced pool
when distributed tickets are enabled; roll the API as a coordinated change.

### Gateway discovery

`TASKFORGE_UPSTREAM_DNS` chooses the Nginx runtime DNS resolver. Current Docker Compose retains
`127.0.0.11` by default; a cluster deployment injects the *discovered* CoreDNS service IP.
This setting is used during initial rendering and certificate-triggered reloads. DNS addresses
must be injected by the deployment system, not compiled into the app image.

The existing names `identity-api`, `tasks-api`, `execution-api` and others are **logical service
names**, not physical node IDs. Deployment must provide equivalent internal service discovery.

## Stage 2: cross-replica workers and real-time delivery

### Background operations

`ai-api` account intelligence and account lifecycle workers no longer reset every
`starting`/`running` row whenever an API replica starts. Each queued or interrupted
operation is claimed under a PostgreSQL **session advisory lock** derived from its ID.
Competing replicas skip an operation that is owned by another live instance. After a
crash closes the owner's database session, the operation can be recovered by a new
worker. Lifecycle receipts in `StepsJson` survive recovery. A dedicated DB connection
is kept open throughout processing and checked every three seconds; the workload
cancels if the lock connection is lost. This is application cooperation, not a
substitute for idempotency of external service writes after uncertain network replies.

The tasks SQL validation dispatcher now serializes each validation receipt using
the existing `PostgresImportLock`. The graph-transfer workers already used this
locking model. Other background loops and external side effects still need review.

### SignalR backplane

Four hub-hosting services (`education-api`, `support-api`, `tasks-api`,
`minecraft-api`) use Redis pub/sub when `Cluster__MultiNode=true`. Each requires
`Cluster__Namespace=<unique-environment-name>` and a reachable Redis connection.
Namespace values are logical environment identities (not node names). For example,
the `taskforge-test` and `taskforge-prod` clusters must use different namespaces
if they share Redis. In the original single-node setup `AddSignalR()` keeps its
in-process implementation with no extra Redis backplane connection.

The new shared source is `services/shared/Cluster/DistributedSignalR.cs` and the
five project files explicitly include it. Docker image-change detection also
watches this file for all four images. A SignalR Redis backplane is NOT shared
presence state, and it does not make non-SignalR event brokers distributed.

### Schema startup safety

When `Cluster__MultiNode=true`, the 12 remaining schema-owning API programs refuse to
start if `Database__MigrateOnStartup=true` (the old default) or
`Database__EnsureCreated=true`. Set BOTH explicit false values in all API
replicas. A separate migration/maintenance job is required before deployment.
The assignments API also skips its one-off assignment backfill and attempt
cleanup on replicated startup; those steps must run in a single maintenance job.
This archive does NOT implement the migration job yet. It deliberately fails
rather than racing schema migrations from every API replica.

## Not yet migration-complete (do not replace production)

- The existing admin Primary switch is still bound to Patroni PostgreSQL leadership. A later
  source change must distinguish `ingress primary` from `database leader` while preserving
  the existing Cloudflare DNS switching contract in the **separate** server-control package.
- API startup now rejects automatic migrations in multi-node mode, but actual single-run
  migration/maintenance jobs still need to be implemented and proven.
- Stateful PostgreSQL, RabbitMQ, Redis, MinIO, the runners' isolation and SQL/1C sandbox
  mounts require dedicated runtime planning, not source-branch hardcoding.
- Browser automation, SignalR connections, background schedulers and other local registries
  must be audited before increasing their individual replica counts.
- The existing code analyzers and language runners (including Python user-code runner) have
  their own isolated runtime; no Python controller is added by this source patch.

No GitHub push, branch creation, production deployment, or database migration is performed
by this patch. Run application tests and the full image build on the user's chosen branch.

## Stage 3: LLM agent retirement

The new source branch intentionally drops the old agent/LLM worker and public assistant UX.
The former `ai-api` image remains for *deterministic account intelligence* only: review of
account similarity, suspicious registrations, decisions, account blocks/merges, and
operation recovery. Its `AccountIntelligenceWorker` and `AccountLifecycleWorker` retain the
Stage 2 PostgreSQL advisory-lock coordination. The module does not call an LLM.

For compatibility with existing account-manager URLs and the EF migration history,
its repository folder, image name `ai-api`, SQL database `taskforge_ai`, and the
`/api/admin/ai/account-manager/*` route are intentionally unchanged for now.
No LLM endpoints, assistant hub or worker processes remain on this image.

See `docs/architecture/LLM_REMOVAL_STAGE3.md` for exact boundaries and remaining
historical database/schema artifacts. This is a source-only change; no live DB schema
or production images have been deleted.

## Stage 4: retire the standalone CT/Masha site

The standalone `apps/web-ct` frontend and `services/tasks/quiz-api` are removed
from this application source branch. They are also removed from build matrices,
legacy Compose, gateway routing/TLS, cluster readiness, browser site registry,
account lifecycle steps and standalone migration tools. `quiz-api` previously
shared the `taskforge_tasks` database, but this stage does **not** delete any
historic tables, migration rows, data or named volumes.

The **main** TaskForge quiz/test tasks and math tasks are unrelated to the retired
CT quiz-api and remain in `tasks-api` unchanged. The Telegram quiz bot is an
independent subsystem and is also retained.

Read `docs/architecture/CT_RETIREMENT_STAGE4.md`. After migration, all old CT
DNS and installed server controller configuration require a separate deliberate
operator cleanup; source changes never change the live Cloudflare configuration.
