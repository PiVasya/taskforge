# Task graph import backend diagnostics

The tasks-api writes an event-driven timeline for each journaled task-graph import.
Logs never include assignment statements, test cases, bearer tokens, or raw JSON payloads.

Filter the production container output by operation ID:

```sh
docker logs --since 30m taskforge-prod-tasks-api-1 2>&1 | grep -E 'TASK_GRAPH_IMPORT_(PHASE|BEGIN|DATA_COMMIT|DATA_REJECT|UNCONFIRMED|MAP_BEGIN|MAP_REJECT|END)'
```

Find gateway timeouts for the same request:

```sh
docker logs --since 30m taskforge-prod-gateway-1 2>&1 | grep -E 'assignments/import-json|upstream timed out'
```

`TASK_GRAPH_IMPORT_PHASE` includes the operation UUID, HTTP trace ID, stage, elapsed
milliseconds and request-abort flag. `TASK_GRAPH_IMPORT_UNCONFIRMED` includes the
current stage, elapsed time, exception type and cancellation state. For normal
assignment imports, progress markers are emitted every 25 assignments; if a
request is cancelled during a specific assignment, its exact 1-based index is
logged. The separate `map.*` markers distinguish slow education-api map saves
from slow tasks-api entity processing.

An import whose effective scopes include only `ids` and `layout` follows the
layout-only path. This path validates existing task/course UUIDs and their
current parent courses, returns the mappings required by the map editor, and
never modifies assignment entities or type-specific specifications. It also
skips sensitive/full assignment snapshots and full readbacks; the audit records
unchanged assignment IDs and the map save records the actual position diff.

A successful data phase results in `data-committed`; the editor then persists
the map with the journaled version check. A 504/499 before the data commit does
not prove that any map write occurred. Use the same operation ID and the
journal resume action, not a second import, to reconcile a pending attempt.
