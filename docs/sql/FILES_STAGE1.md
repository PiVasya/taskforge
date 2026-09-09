# Source change inventory at the SQL migration boundary

Current package: `taskforge-develop-sql-domain-stage1-fix1`.

## Modified original files (relative to develop 211)

- `00_AI_READ_THIS_FIRST.md`
- `docs/README.md`
- `services/execution/api/Data/ExecutionDbContext.cs`
- `services/execution/api/Domain/ExecutionModels.cs`
- `services/execution/api/TaskForge.Execution.Api.csproj`
- `services/solutions/api/Data/SolutionsDbContext.cs`
- `services/solutions/api/Domain/SolutionSubmission.cs`
- `services/solutions/api/TaskForge.Solutions.Api.csproj`
- `services/tasks/assignment-api/Data/TasksDbContext.cs`
- `services/tasks/assignment-api/TaskForge.Tasks.Api.csproj`

## Original documentation relocations

- `README.md` -> `docs/README.md`
- `TASKFORGE_104_CI_LEGACY_HA_CLEANUP.md` -> `docs/history/TASKFORGE_104_CI_LEGACY_HA_CLEANUP.md`
- `TASKFORGE_105_CLUSTER_HA_OBSERVABILITY.md` -> `docs/history/TASKFORGE_105_CLUSTER_HA_OBSERVABILITY.md`
- `TASKFORGE_106_CLUSTER_PRIMARY_UI_BOT.md` -> `docs/history/TASKFORGE_106_CLUSTER_PRIMARY_UI_BOT.md`
- `TASKFORGE_107_PRIMARY_SWITCH_LIVE_PROGRESS.md` -> `docs/history/TASKFORGE_107_PRIMARY_SWITCH_LIVE_PROGRESS.md`
- `TASKFORGE_108_PRIMARY_SWITCH_LOCAL_PATRONI.md` -> `docs/history/TASKFORGE_108_PRIMARY_SWITCH_LOCAL_PATRONI.md`
- `TASKFORGE_98_CI_FIX_AUDIT.md` -> `docs/history/TASKFORGE_98_CI_FIX_AUDIT.md`
- `TASKFORGE_CLUSTER_DASHBOARD_UPDATE.md` -> `docs/history/TASKFORGE_CLUSTER_DASHBOARD_UPDATE.md`
- `TASKFORGE_CLUSTER_DASHBOARD_VALIDATION.md` -> `docs/history/TASKFORGE_CLUSTER_DASHBOARD_VALIDATION.md`

The eight historical reports are byte-identical; README only has its Markdown link paths adjusted.
All 86 original migrations/snapshots and both baseline manifests remain unchanged.
The additional stage handoff is at `docs/sql/MIGRATION_HANDOFF.md`, not at the root.

## Added implementation / verification areas

- `services/tasks/assignment-api/Domain/Sql/`: SQL aggregates, keys and access rules.
- `services/tasks/assignment-api/Data/Sql/`: mappings and SaveChanges guards.
- Three `Data/*DbContextFactory.cs` design-time factories.
- `services/execution/api/Domain/ExecutionJobKinds.cs`.
- `.config/dotnet-tools.json`: existing handoff CLI pin (10.0.8).
- `tools/sql-domain-check/`: 44 prepared executable .NET checks, not run here.
- `scripts/check-sql-domain.sh`: local restore/build/check gate, no migration generation.
- `scripts/ci/check-sql-migration-boundary.py`: immutable baseline/source/relocation guard.
- `scripts/ci/check-sql-ef-dependencies.py`: explicit and restored dependency graph checks.
- `scripts/ci/test-sql-ef-dependencies.py`: 19 executed guard regression tests.
- `docs/sql/`: handoff, design documents, baseline hashes and factual QA logs.

For this correction alone, SQL Entity/DbContext code is unchanged from the first stage-1 archive.
Actual results and limitations: `./QA_EF_RUNTIME_FIX.md`.
