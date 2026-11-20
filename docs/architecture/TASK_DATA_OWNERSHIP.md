# TaskForge K3s: single owner of attempts (local lab)

**Decision (2026-11-20): retain Solutions API and move test/math attempts out of Tasks.**
This branch is for a **new, isolated YugabyteDB on the developer computer**, not
for migration from production or a historical deployment. No production backfill is required. The source keeps the existing code/SQL/image
submission owner (Solutions), and moves test/math attempt persistence to Solutions.

## One owner per data category

| Data / lifecycle | Physical owner | Table(s) |
| --- | --- | --- |
| Course structure, maps, groups, access | Education API | `Courses`, `CourseMaps`, `Groups` and related |
| All five task definitions, tests, SQL datasets, authored specs, graph import/export | Tasks API | `Assignments`, typed spec tables, SQL catalog |
| Code and SQL submissions | Solutions API | `SolutionSubmissions` |
| Image solutions | Solutions API | `UserImageTaskSolutions` |
| Test and mathematics attempts | Solutions API | `TaskAttempts` |
| Rating, badges, quotas, administrative result feed | Solutions API | rating tables, `AdminSolutionFeedEvents` |
| Running code and SQL | Execution API | execution jobs/results, not user submissions |

The same physical YugabyteDB instance can host **separate logical service databases**.
`taskforge_k3s_tasks` has no attempt or admin-feed tables in the *target EF
model*. `taskforge_k3s_solutions` is the only source of truth for user results.

## Request flow

- `test`/`math`: public route in Tasks -> Tasks checks authoring spec and
  grades answers -> authenticated internal Solutions attempt API persists start,
  draft and final outcome. Writes, rating invalidations, and admin events are
  within the Solutions transaction. Tasks does not persist a second copy.
- `code`/`sql`/`image`: existing public Solutions routes -> Execution and/or
  Tasks grading contracts -> Solutions persists final result. No relay to Tasks.
- Course progression: Tasks asks Solutions for solved assignment IDs (one
  batched request); no direct cross-database reads and no shadow attempt table.
- The rating worker is deployed in BOTH local `core` and `all-api` profiles; it reads all five attempt kinds in the Solutions database and
  queries Tasks only for *assignment metadata* (course/rating), not attempts.
- Analytics, public test/math reviews and profile statistics: Tasks may serve
  compatibility routes; the underlying attempts and SQL aggregation are in
  Solutions.
- Administrative SSE: one durable Solutions outbox, one EventSource.
  `/api/admin/task-solution-events` is a backwards-compatible Solutions alias.

## Generate migrations (no database writes)

Earlier EF migrations are historical source-controlled
artifacts, NOT proof that the new model is migrated. After extracting this ZIP,
**before** building or booting Kubernetes, on a machine with .NET 10 SDK:

```bash
bash scripts/generate-migrations.sh ConsolidateAttempts tasks,solutions
```

The generator targets **two services** (one migration per changed DbContext):

- **Tasks:** drops the historical `TaskAttempts` table and its indexes. The
  cancelled `UnifiedTaskAttempts` and Tasks admin-feed migrations have been
  removed from this previously uninitialized K3s branch, so they must not be
  recreated or dropped by a new migration.
- **Solutions:** creates `TaskAttempts` with JSONB columns and indexes for
  test/math history and active-attempt uniqueness. Existing code/SQL/image
  submissions, rating and the admin feed stay in Solutions.
- **Education:** no migration is needed. The unused task-definition staging
  migration was removed; course/group/map schema stays unchanged.

Only abandoned **K3s sandbox** staging migration pairs were removed. All older
schema history and the existing Solutions admin-feed migration were retained.

These migrations are safe to generate **without** a running database. Applying
migrations is a separate explicit action against disposable lab databases.
`scripts/ci/check-ef-migrations.py` must fail if EF reports pending model changes;
CI and `yugabyte-smoke.sh verify` are meant to block an incomplete cutover.

## Required local validation before trusting this code

1. Generate and inspect both EF migrations above; confirm no unrelated
   tables are dropped, and check migration history with CI scripts.
2. Run `dotnet test` for Tasks, Solutions and rating-worker tests in Release.
3. Start the disposable YugabyteDB, run migration jobs, and execute
   `bash scripts/ci/yugabyte-smoke.sh verify` as part of the lab CI flow.
4. Test test/math: start, refresh draft, submit, retry same verdict, review,
   expire, attempt limit, bulk delete, account merge/delete.
5. Test code, SQL and image: attempts, verdict changes, progress, rating,
   import/delete of an assignment, and admin SSE replay after reconnect.
6. Check both Kubernetes generation profiles and front-end interactions.

**Not performed in the patch-builder environment:** dotnet build/test and real
YugabyteDB migration; that environment has neither .NET SDK nor Docker.
The archive is SOURCE code, not a validated deployable image.
