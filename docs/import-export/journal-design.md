# Durable task-graph import

## Release prerequisite

This source changes `TasksDbContext` and `EducationDbContext`. Existing migrations
and ModelSnapshot files are user-owned and remain unchanged. Generate and commit
new migrations locally **before deploying either API or passing the EF semantic gate**:

```bash
bash scripts/generate-migrations.sh AddTaskGraphImportJournal assignment,education
```

Apply migrations through the normal deployment migration stage. Roll out
education-api first, tasks-api second, front third. The cumulative archive also
retains the earlier support-api/support-bot delivery fixes; update those in that
order when upgrading directly from original develop(308).

Do not deploy the journal-enabled APIs against a database missing the new tables.
There is no application startup DDL, bypass of EF validation, or changed migration
history in this release.

## Guarantees and limits

A logical import has a client-generated UUID in the HTTP envelope:
`{ operationId, graph, options }`. It is not a new field in the canonical graph.
The UUID is bound to the authenticated user, course and canonical request hash.
JSON object member order/formatting is ignored; array order and string contents
remain significant. A different graph or options under the same UUID is rejected.
An intentional new operation remains a new import: missing task IDs mean new tasks.

The journal reserves task/course IDs before writing content. PostgreSQL session
advisory locks prevent overlapping writers for the same operation; an additional
reservation lock and active-operation check prevent two tabs of the same user
from starting competing unfinished imports in the same course. Locks are not
expiring leases. They release with the database connection.

The tasks transaction includes task/spec/dataset changes and its response receipt.
Nested-course creation is a separate Education API transaction. The persisted IDs
allow it to be retried without inventing new entities after a partial failure.
The assignment content baseline detects changed or newly occupied task identities
when an unfinished canonical data operation is resumed. This is not a replacement
for optimistic concurrency in every independent task editing endpoint.

The map checkpoint is stored before forwarding the write. Education API stores
both the map and its operation receipt in one transaction, preserving the existing
expectedVersion compare-and-swap. A lost acknowledgement is retried with the same
checkpoint. A saved receipt is returned before attempting a second write, even
when a different editor has subsequently saved a newer map. Realtime notification
failure after commit is diagnostic, not a rollback.

A confirmed version conflict requires a new explicit map-only preview. A map lock
busy response, auth outage or an unknown network result must not be interpreted
as proof that the original write did not commit. No automatic overwrite or
compensating deletion occurs. `stop` requires explicit acknowledgement and is
blocked while the map write outcome remains unknown.

**There is no global distributed transaction or automatic rollback across services.**
Partially created courses can exist before task commit; committed tasks can exist
before map commit. The supported alternative is a durable, discoverable operation
with repeatable stages. Stopping does not undo content, delete created courses or
restore deleted tasks. Canonical imports have stricter binding/baseline checks
than compatibility-only legacy input shapes.

## Endpoints

All routes below use ordinary editor authentication and re-check course/subtree
access. An operation is readable/resumable only by its initiating user.

Prefix: `/api/courses/{courseId}/assignments/import-json`

| Method | Suffix | Purpose |
|---|---|---|
| POST | empty | Reserve/run/replay the same operation |
| GET | `/operations` | Last 30 operation summaries for user/course |
| GET | `/operations/{id}` | Durable status and receipts; no-store |
| POST | `/operations/{id}/resume` | Continue the same saved stage |
| PUT | `/operations/{id}/map` | Store/forward a map checkpoint |
| POST | `/operations/{id}/stop` | Stop without rollback; `acknowledgePartial: true` |

Map body: `{ expectedVersion, document, replaceConflicted?: true }`.
Replacement is accepted only for a confirmed rejected/conflicted checkpoint.
The ordinary Education map endpoint accepts an optional `operationId`; legacy
callers remain compatible but do not gain the new replay guarantee automatically.

## Audit and storage

The database journal stores the prepared payload and response, including sensitive
authoring content, under normal editor access controls. It is not a public report.
The browser stores only the operation UUID in a user/course-namespaced localStorage
entry; server history also supports recovery when browser storage is unavailable.

The downloaded report is allowlisted metadata: request hash, scopes/options,
assignment IDs/actions, changed field names, before/after semantic hashes,
deletions, course IDs, saved map and map diff. It excludes raw input, test answers,
SQL reference/seed scripts and arbitrary HTTP error/option properties. It can still
identify private course/task UUIDs and must be shared appropriately. Detailed raw
Preview data is not retained after reload; durable entity/map results are.

Journal/receipt rows do not have an automatic expiry in this implementation. Never
purge a live operation or a receipt needed for replay independently of the other
service. Account/course deletion and a future retention policy need explicit
operational handling; the UI history limit is not a database retention limit.

## Verification

Model regression suite:

```bash
node --test apps/web/scripts/sql-tests/*.test.mjs \
  apps/web/scripts/agent-tests/*.test.mjs \
  apps/web/scripts/admin-solutions-tests/*.test.mjs \
  apps/web/scripts/landing-tests/*.test.mjs \
  apps/web/scripts/frontend-diagnostics-tests/*.test.mjs \
  apps/web/scripts/course-map-tests/*.test.mjs \
  apps/web/scripts/cluster-tests/*.test.mjs
node apps/web/scripts/check-architecture.js
bash scripts/tests/repository.sh
```

Build and backend tests require the repository's .NET SDK/npm dependencies:

```bash
bash scripts/tests/dotnet.sh
bash scripts/tests/frontend.sh
```

Opt-in real HTTP acceptance, **only against an empty hidden ROOT QA course**:

```bash
read -rs -p 'QA editor token: ' TASKFORGE_TEST_TOKEN; echo
export TASKFORGE_TEST_TOKEN
node scripts/tests/task-graph-import-live.mjs \
  --base https://YOUR-TASKFORGE-HOST \
  --course-id YOUR-EMPTY-HIDDEN-ROOT-COURSE-UUID \
  --allow-writes --large 250 --out /tmp/taskforge-import-live.json
unset TASKFORGE_TEST_TOKEN
```

This script uses the production graph reducer/serializer and real service calls.
It checks mega import, same-ID replay, changed-payload rejection, durable map
receipt, later-map preservation, branch creation, exact test strings, checks-only
updates, invalid-reference refusal and real deletion of its own synthetic branch.
The optional 250-node case applies and re-reads the graph, not just Preview.
Synthetic course content remains for inspection; the script refuses a nonempty
course on rerun. It never extracts tokens from diagnostic archives.

Not covered by this HTTP script: actual browser feature orchestration, process
crash injection at each database boundary, arbitrary concurrent editor mutations,
all SQL engine/profile executions, production latency or maximum JSON/request
sizes. Those are explicit acceptance gaps, not passed tests.
