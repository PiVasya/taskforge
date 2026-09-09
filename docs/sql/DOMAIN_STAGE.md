> Historical stage-1 record. The user generated the migrations in develop(212).
> Do not repeat migration generation; see [current deployment](DEPLOYMENT.md) and [runtime QA](QA_RUNTIME.md).

# SQL domain: migration boundary

Status: source/model preparation only. This is not a deployable SQL feature release.
The unmodified requested plan is in `SQL_UPDATE_PLAN_TASKFORGE.txt` in this directory.
This document records the implementation choices that refine that plan; it does not
claim that the subsequent API, runtime, editor or cluster work is implemented.

## Inspected baseline

Actual source: `taskforge-develop(211)(1).zip`, not the older filename in the plan.
SHA-256: `b32896d2363f0c106239d88250a779aaeb0bd448b616398f86560b23f14be53e`.

Actual production: `taskforge-prod-linux-v40-cluster-control-final-r57.tar(4).gz`.
SHA-256: `4f49e6f2485afe6f139a626f89a56d6f6646bbaff249d3c1e27a3223b2d8f705`.
The duplicate uploads `(211).zip` and `r57.tar(3).gz` have identical respective hashes.

Read before editing: `00_AI_READ_THIS_FIRST.md`, logging policy, the three domain
models/DbContexts, API startup and internal judge/claim/submission paths, task-graph
serialization and frontend type selection, Compose/workflow inventories, and r57
agent/primary/inventory/readiness code. Root README's historical v30 summary is not
the authority for the supplied production r57.

Repository facts that affect the model:

- `Assignment` has no author/tenant FK. Staff access is the existing global
  Admin/Editor/LearningEditor policy; learner access is course/progression based.
- All three business contexts use Npgsql; they apply migrations at web startup.
- Existing execution jobs require a graded `SubmissionId` and claim-next does not
  filter by worker capabilities. Both code and image paths must remain compatible.
- Existing submissions are created before fetching their judge spec. The future SQL
  submit path must fetch and pin an immutable spec BEFORE persisting its binding.
- r57 explicitly defines A/B full and C lite; SQL cache must never enter its
  Patroni/storage identity or Cloudflare/public readiness decisions.

## Eight new tables in TasksDbContext

| Entity | Role |
| --- | --- |
| SqlDataset | Reusable catalog entry, owner, private/editor-library scope, archive flag. |
| SqlDatasetVersion | Immutable definition, seed, dataset-level engine mappings and content hash. |
| SqlEngineProfile | Immutable exact runtime/build digest, adapter version and semantic settings. |
| SqlAssignmentSpec | One-to-one assignment root with separate draft/published revision pointers. |
| SqlAssignmentSpecVersion | Immutable mode, rules, limits, reference SQL and verifier version. |
| SqlAssignmentEngineTarget | Immutable member of a spec version with per-engine verification overrides. |
| SqlDatasetEngineValidation | Durable compatibility receipt for dataset version and engine profile. |
| SqlExpectedArtifact | Separate assignment-target validation receipt and private expected artifact. |

Private SQL content is not attached as a navigation on the existing Assignment
entity and is not inserted into `Assignment.TestsJson`. Existing Assignment code
and its current mappings remain unchanged. `[JsonIgnore]` is an additional safeguard
on reference/expected fields, not a replacement for explicit learner DTO projection.

### Ownership

The initial catalog scopes are `private` and `editor-library`, not invented tenants.
A private dataset needs a real owner id; a shared catalog entry can be unowned,
in which case changing its sharing scope is Admin-only. Owner ids are external identity references, not cross-service FKs.
Only an authenticated staff owner or Admin can change sharing. Staff can manage
editor-library entries; learners cannot query the catalog, including their own
non-staff account's entries. Archived datasets cannot be newly reused.

These domain policy functions are prepared but **not yet wired to HTTP endpoints**.
Account merge/delete handling must transfer ownership or archive/anonymize the
catalog entry in the next stage. Immutable dataset/spec rows intentionally contain
no creator user ids that would need later rewriting for account erasure.

### Revision and publication rules

Every saved spec revision, including a draft revision, is immutable. A root points
to the last saved draft and independently to the published version. Editing does
not silently replace the published rules. Version numbers are unique per parent;
allocate them in the same transaction as a concurrency-checked root/catalog update.

Composite `(AssignmentId, VersionId)` FKs make it impossible to point a draft or
publication at a revision of another assignment. The API must insert a new root
with null pointers first, then insert the revision AND its engine targets in one
SaveChanges batch, then update pointers in the same database transaction. This
avoids a new-root/new-version FK cycle during insertion.

Targets cannot be appended to a stored revision: SaveChanges rejects a target
unless its parent revision is Added in the same batch. Updates/deletes of immutable
content are rejected by the context, and immutable properties have Throw after-save
behavior. Both synchronous and asynchronous SaveChanges paths are covered.

These are ORM/application write guarantees plus relational FK/unique/check
constraints. They are NOT database update triggers. Do not use ExecuteUpdate,
ExecuteDelete or raw SQL to bypass these guards. Explicit historical retention and
account/catalog lifecycle workflows remain future work. All SQL history FKs use
Restrict; do not casually cascade-delete an assignment with SQL history.

Mutable catalog, root and validation receipts have application-rotated optimistic
concurrency stamps. API conflict handling and callback fencing must still be wired.
A successfully validated expected artifact is sealed against tracked updates and
removal: changed grading rules require a new spec version.

### Three independent notions of readiness

1. Dataset/engine compatibility: SqlDatasetEngineValidation.
2. Assignment/spec/engine expected artifact validation: SqlExpectedArtifact.
3. Local GOLDEN/READY cache presence: worker telemetry only, not a business table.

`valid` means a durable validation receipt, not that a particular node currently
has a warm database. Losing every SQL cache volume must not invalidate published
content or force a Primary change. `ValidationRunId` and the concurrency stamp are
prepared for fencing obsolete callbacks; their endpoint enforcement is still pending.

An expected artifact can be inline JSON for small cases or a private relative
object-storage key, never both. A valid receipt requires its content hash, byte
length, validation time and content location. Learner responses must not expose it.
No fixed physical GOLDEN database implementation is imposed on MySQL or SQLite.

### Content identities

`SqlContentKeys` defines a versioned SHA-256 envelope. Object keys are sorted with
ordinal comparison; arrays retain order. Decimal JSON number tokens normalize
exactly without conversion through floating point. Duplicate object keys and
out-of-bounds documents/exponents are rejected during hashing.

Dataset identity includes definition schema version, logical definition, seed and
dataset-level engine overrides; catalog ids, name and version number do not enter
that content identity. Thus identical datasets can share local materialization
while retaining separate ownership and validation receipts.

Runtime profile identity includes engine, exact version/build digest, adapter
version, settings schema version and semantic settings. No fabricated production
digests, unpinned runtime registrations or engine catalog seed rows are supplied.
Worker registration must resolve defaults (encoding, collation, timezone, SQL mode,
platform and SQLite build flags) and reject runtime/profile mismatches.

Spec identity includes dataset version id, mode, all check documents, starter and
reference SQL, multi-statement setting, limits, verifier version and the full target
set including overrides. A null override inherits; a JSON object replaces the
whole corresponding document. Type/default mapping overrides are dataset-owned,
not assignment-owned, so assignments can safely reuse a materialization.

Expected identity ALSO includes immutable spec-version id and target id, the
materialization identity and expected format version. Two assignments sharing a
dataset cannot accidentally share grading answers. This is intentionally stricter
than deduplicating expected results across assignments.

Portable schema/type/default normalization and actual SQL result semantics are not
implemented by this JSON hashing helper; they belong to the next domain/API stage.

## ExecutionDbContext

Added fields: Kind, Target, PayloadVersion, PayloadJson, DeduplicationKey,
ClaimedByWorkerId, LeaseToken, LeaseExpiresAt. SubmissionId becomes nullable.

`legacy` is the explicit default for every existing job. There is no migration
which guesses image/code kind by inspecting Language or TestsJson, and no automatic
SQL engine assigned to historical work. Existing creation/claim DTOs are unchanged
at this boundary; the SQL path is not exposed yet.

New typed jobs require a nonempty target and a versioned payload. SQL preview and
materialization have no graded submission; SQL check requires a submission,
assignment and user. SQL jobs cannot use legacy tests/code-policy JSON columns.
Lease fields are all-null or all-present. The actual capability filter,
lease-aware completion, retry fencing, idempotency protocol, RabbitMQ wake-up,
fallback polling and preview endpoints must be implemented AFTER the migrations.
Do not deploy a SQL producer against the existing unfiltered claim endpoint.

`DeduplicationKey` must be generated internally from a scoped producer operation;
never trust an arbitrary globally unique key directly from a public client.

## SolutionsDbContext

Added nullable fields: ExecutionTarget, SqlSpecVersionId, SqlEngineProfileId.
Legacy rows stay unbound. SQL binds both immutable spec and engine profile and
cannot change that binding via tracked updates. There are no cross-service FKs.
Source SQL continues to use the existing Code field, not a parallel attempt table.
When the APIs are added, SQL display/dispatch must use ExecutionTarget rather than
pretending a database engine is an existing code Language.

## Remaining runtime decisions and implementation gates

- No new public sql-api; retain tasks/solutions/execution domain ownership.
- Long-lived, resource-limited server engines, a bounded ready pool and background
  replenishment. No cold engine/container or full datadir copy on each attempt.
- A shared engine is not per-attempt CPU/RAM isolation. Bound admission and
  shard-wide CPU/memory/temp/connection limits independently of task energy quotas.
  Choose shard counts only after measuring A/B resources. SQLite untrusted SQL
  must not run inside the credential-bearing coordinator process.
- Define exact result/state/schema comparison, multi-result-set behavior,
  transaction/partial-failure semantics and portable identifier/default rules
  before exposing publish. A SQL blacklist or semicolon split is not a sandbox.
- Run always starts from the chosen dataset version and is independent of previous
  Run calls. Preview is collected after execution and before sandbox destruction.
- Publication requires every enabled engine target's dataset receipt and expected
  artifact to be valid for the pinned versions. No student Check may silently
  publish/materialize an unvalidated spec.
- First integrate capability-aware claims and lease fencing, then SQL producers
  and workers. Keep the old attested code/image judge paths intact.
- API ownership and safe projections, standard submission/rating progression,
  worker adapters/pools/verifiers/limits, frontend, task-graph v5 with legacy import,
  CI/images/Compose and production are all outstanding.
- A/B support SQL runtime; C remains lite. SQL outages are auxiliary degradation,
  not a reason to fail over business Primary or reissue public certificates.
  Local SQL caches never enter Patroni, re-home or persistent business-volume lists.
- Final source and cluster revision must each be self-contained. Production r57
  was not changed or repackaged as an alleged r58 in this stage.

## Verification and next action

See `./MIGRATION_HANDOFF.md` for exact commands and
`QA_STAGE1.md` for results and limitations. The added .NET test executable exercises
model metadata and domain rules with database saves intercepted. It does not call
EF migration generation, apply migrations or execute student SQL. A real
migration/constraint integration test against a disposable business database still
belongs after the user's generated migrations have been reviewed.
