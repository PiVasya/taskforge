# Task graph transfers

## Deployment

This source package is based on `taskforge-develop-20261026-graph-import-diagnostics-fix.zip`.
It changes the tasks database model. The migration belongs to the project owner;
no migration or existing ModelSnapshot is generated or edited in this package.

Run from the repository root with the SDK selected by `global.json`:

```bash
chmod +x scripts/generate-migrations.sh
./scripts/generate-migrations.sh AddTaskGraphTransfers assignment

dotnet test services/tasks/assignment-api/tests/TaskForge.Tasks.Api.Tests/TaskForge.Tasks.Api.Tests.csproj
dotnet test services/education/api/tests/TaskForge.Education.Api.Tests/TaskForge.Education.Api.Tests.csproj
node --test apps/web/scripts/*-tests/*.test.mjs
node apps/web/scripts/check-architecture.js
npm --prefix apps/web ci
CI=true npm --prefix apps/web run build
```

Review and commit the newly generated migration and snapshot before CI/deployment.
The semantic migration check is expected to fail until this is done. Apply the
migration using the normal tasks-api deployment/migration mechanism. Deploy
`tasks-api`, `education-api`, `front` and `gateway` from the same source revision;
update backend services before publishing the new frontend. The migration creates
`TaskGraphTransfers` and `TaskGraphTransferParts`. No education migration is needed.
Do not deploy the new tasks-api against a database without these tables.

There is no new container, broker, Node Agent operation, cron job or external
queue. The existing tasks-api hosts the worker. The existing standalone cluster
package remains authoritative; source-tree historical cluster scripts are not a
replacement for it. Keep existing internal keys and debug-log defaults unchanged.
Back up the database before applying any production migration.

## Execution protocol

The UI submits a short authenticated POST and receives HTTP 202 plus a transfer
UUID. Work then runs with its own cancellation token, not RequestAborted from the
browser. Status reads and result downloads are independent endpoints. A gateway
failure during status polling is not evidence that the worker rolled back.

Endpoint base: `/api/courses/{courseId}/assignments/transfers`.

- `POST /`: `{ transferId, kind, payload, operationId? }`.
- `GET /`: active jobs first, then recent history; up to 30 rows for this actor/course.
- `GET /{transferId}`: status without the retained input or result bytes.
- `GET /{transferId}/result`: ready JSON artifact, with no-store/nosniff headers.
- `POST /{transferId}/cancel`: cooperative cancellation, not rollback.
- `POST /{transferId}/retry`: requeue the same failed/cancelled, unexpired job.

Kinds: `export`, `snapshot` (a selective snapshot for Preview), `preview`, `import`,
`resume`, `map`. Import payload is `{ graph, options }`. Export/snapshot payload
contains the existing `includeIds`, `includeContent`, `includeChecks`,
`includeVisibility`, `includeConnections`, `includeConnectionAccess`,
`includeLayout`, `includeGuide` flags. The map payload is the existing expected
version/document request. Resume uses the existing import operation UUID.

A repeated transfer UUID with the same request replays that job's status. A
changed request with that UUID returns `TRANSFER_PAYLOAD_CHANGED` (409). A transfer
owned by someone else is not exposed. Import operation UUIDs continue to bind the
durable data/map journal; a transfer UUID is a transport execution identifier,
not a substitute for the existing import operation identifier.

The browser reconciles an uncertain POST by reading its already allocated UUID.
Any resubmission uses that same UUID. Status/download retries do not repeat the
mutation. Polling has bounded backoff; exhausted retries report uncertainty and
leave the job discoverable in history. Stopping observation is not cancellation.

Existing synchronous HTTP endpoints remain available for compatibility. Their
long-request behavior has not magically changed for clients that still use them;
the new UI uses the transfer endpoints instead.

## Import completion and recovery

The transaction boundaries remain explicit:

1. Preview and selective current-state snapshot.
2. Existing journaled data import and durable result.
3. Client's existing tested graph reducer projects the map.
4. A separate durable map job writes through education-api and verifies its receipt.

This is NOT a distributed transaction across tasks-api and education-api.
An import is complete only when its import journal reports `completed`, after the
map receipt is verified. UI percentages for the data stage stop at 70%; the map
stage stops at 99% until the journal confirms completion. Percentages are weighted
stage progress, not elapsed-time estimates. Object counters are actual completed
work. Result serialization reports compressed bytes written.

Closing a modal does not stop submitted backend work. A browser reload restores
job history. If the tab closes between the data stage and submission of the map
stage, the data journal remains `data-committed`: use **Continue** in the existing
import journal to calculate/confirm the map. The server does not independently
reimplement the frontend map reducer. This limitation is intentionally visible,
not disguised as a successful 100% import.

If the map changed, use the existing map conflict/repreview flow. Never bypass
expectedVersion, generate a fresh operation after an uncertain write, or assume
cancel means previously committed data has been undone. Retrying a map job reuses
the journal's exact durable checkpoint. A rejected/conflicting map requires the
explicit repreview flow rather than a blind retry.

## Lightweight paths

UI presets:

| Preset | Included data |
| --- | --- |
| Coordinates | UUID references and layout/viewport |
| Map structure | UUIDs, visibility, connections, connection effects, layout |
| Conditions without tests | UUIDs and content |
| Full export | Existing canonical data categories, without the optional guide by default |

Canonical `taskforge-task-graph` schema version 5 remains unchanged. Existing
UUIDs, explicit course ownership checks and connection merge/replace semantics
are preserved. Omitted task.course preserves actual ownership; layout imports
cannot relocate an assignment between courses. Metadata imports cannot create
an empty assignment when its UUID is missing/new.

Metadata export uses an explicit assignment projection and never invokes typed
code/image/test/math spec loading or SQL dataset export. Conditions export does
not request code test payloads or quiz/math answers. Heavy typed data is loaded in
batches of 100 when requested. The optional large guide is disabled by default.
Preview requests only content/check scopes that could actually be updated.
Current titles/types needed for display are obtained separately as small metadata.

Metadata-only updates use a separate application path. Existing assignments do
not run SaveCodeFromRequestAsync merely because their coordinates are changing.
Visibility changes retain publication checks and rating invalidation. Layout-only
journal snapshots/audits do not materialize assignment contents. Schema-1 journal
hashes preserve their legacy semantics; new selective hashes are marked schema 2.

Preview reachability now describes the resulting map, including preserved links.
A layout JSON without a connections array no longer means 'there are no paths'.
The projection resolves nodes by UUID even when local JSON keys were renamed.

The full export still builds a canonical JSON object on the server; it is not a
constant-memory export engine. The download is streamed from compressed parts,
and the frontend downloads a Blob rather than parsing and re-stringifying that
full object. Preview of a full-content import still requires the content selected
for comparison. Large full imports still need appropriately sized batches.

## Worker, access and retention

There are two independently serviced lanes per tasks-api replica: `metadata`
and `content`. Coordinate/structure exports, metadata imports/previews and map
writes do not wait behind full-content exports on the content lane. Resume jobs
use the content lane conservatively. Multiple replicas coordinate each job with
a PostgreSQL session advisory lock, not an expiring timestamp lease.

A worker heartbeat persists phase, percentage, counters, current UUID and byte
count approximately once a second. Lost heartbeats are surfaced, not presented as
success. On graceful shutdown, work is requeued where possible; after an unclean
shutdown a surviving/new worker can reclaim a running job whose PostgreSQL lock
was released. Existing import journal idempotency prevents duplicate data/map
application on recovery. This behavior still requires real PostgreSQL/failover QA.

Actors are restored from current Identity roles and lifecycle state; browser JWTs
are not retained in transfer records. Blocked, deleted, merged and unauthorized
accounts are rejected. The worker-only capability lives in HttpContext.Items and
cannot be supplied by client headers. The internal education map endpoint is
protected by the existing internal-key middleware and uses the same course access,
optimistic version and idempotent receipt checks as the public map endpoint.

Results are gzip-compressed, stored in 256 KiB database parts, and only exposed
after successful finalization. Downloads take a shared advisory lock; retention
cleanup takes the exclusive lock and rechecks state before deleting parts.
Incomplete results cannot be downloaded. Status queries never select PayloadJson
or binary result parts. New structured phase logs do not contain task bodies.
Existing repository-wide debug middleware policy is not disabled by this update.

Configuration (ASP.NET configuration keys / environment variable equivalents):

| Key | Environment | Default | Bound |
| --- | --- | ---: | --- |
| GraphTransfers:QueueCapacity | GraphTransfers__QueueCapacity | 64 | 2..1000 active jobs globally |
| GraphTransfers:MaximumMinutes | GraphTransfers__MaximumMinutes | 60 | 1..240 per execution attempt |
| GraphTransfers:RetentionHours | GraphTransfers__RetentionHours | 24 | 1..168 after attempt completion |

Additional bounds: 6 active jobs per user; 24 MiB UTF-8 request payload; 512 MiB
compressed artifact per job; existing maximum 5000 graph tasks. Retention checks
run about every five minutes. Expired payload/results are removed, and expired
status rows are removed seven days after expiry. Active jobs are not expired by
cleanup. Storage is bounded per job, not by a global byte quota: provision and
monitor database storage/replication for the chosen concurrency and retention.

## Diagnostics

Filter tasks-api logs for:

```bash
docker logs --since 30m taskforge-prod-tasks-api-1 2>&1 \
  | grep -E 'TASK_GRAPH_TRANSFER_|TASK_GRAPH_IMPORT_(PHASE|BEGIN|DATA_COMMIT|MAP_|END|UNCONFIRMED)'
```

Useful new events are TRANSFER_BEGIN, TRANSFER_PHASE, TRANSFER_END,
TRANSFER_FAILED, TRANSFER_INTERRUPTED, TRANSFER_HEARTBEAT_FAILED,
TRANSFER_QUEUE and TRANSFER_RETENTION. Correlate transferId, operationId, courseId,
phase, current UUID, elapsed time and attempt number. Worker errors preserve a
safe code in status while the exception stack stays in backend logs. A dependency
failure, cancellation, execution timeout and lost heartbeat are distinct states/
codes; none should be diagnosed solely from a Cloudflare error page.

The new gateway result route streams directly without response buffering. It does
not extend a mutation timeout to hide a slow synchronous handler.

## Verification and acceptance

Executed in the packaging environment:

- All dependency-free web model suites: 229 passing tests.
- Course-map subset: 117 passing tests, including 20 additional regression cases.
- Frontend architecture and route/Caddy invariants.
- Full `bash scripts/tests/repository.sh` boundary suite.
- Syntax parsing and local import resolution of changed JavaScript/JSX/MJS files.
- Original migration/ModelSnapshot byte comparison and archive integrity check.

Not executed here: .NET compilation/xUnit, React production build/Jest, live
PostgreSQL queue integration, browser rendering and deployed end-to-end tests.
The packaging environment has no .NET SDK or installed frontend dependencies;
source review and Node tests are not substitutes for those release gates.

Added `GraphTransferTests.cs` covers effective lane selection, payload-free status
SQL, explicit light export projections, metadata export with an intentionally
unreachable DB, progress concurrency, fail-closed actor restoration, unforgeable
worker capability, safe error classification and result-part model relationships.
These tests must be compiled/executed in CI with the pinned SDK.

The existing destructive live smoke suite has an explicit asynchronous transport
mode. It refuses nonempty/shared/public courses and requires opt-in credentials:

```bash
TASKFORGE_TEST_TOKEN='<editor token>' node scripts/tests/task-graph-import-live.mjs \
  --base https://YOUR_TEST_HOST \
  --course-id EMPTY_PRIVATE_ROOT_COURSE_UUID \
  --allow-writes --transfers --large 250
```

Use a disposable test course and a test environment, never a production archive
course. The script writes synthetic fixtures and leaves its dedicated course for
inspection; it does not delete the course. The asynchronous mode routes the same
existing import/map acceptance scenarios through transfers and additionally
checks transfer UUID replay/conflicts and metadata-only export. `--help` performs
no network requests or writes.

Before rollout, also exercise process restart mid-export, gateway disconnection
after job acceptance, cancellation during data import, concurrent tabs, map version
conflict/repreview, access revocation, result expiry and old prepared-operation
resume. Compare full exports before/after a layout-only import: UUIDs, conditions,
test cases, visibility and connections must remain unchanged.
