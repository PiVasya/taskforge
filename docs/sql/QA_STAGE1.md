# SQL domain stage 1: actual verification record

> Historical record for the first stage-1 archive, before the reported CS1705 failure.
> Current package checks and remaining limits are in `./QA_EF_RUNTIME_FIX.md`.

Date: 2026-09-09. Stage: migration boundary, not a deployable SQL release.

## Baselines and change scope

Source archive inspected: `taskforge-develop(211)(1).zip`.
SHA-256: `b32896d2363f0c106239d88250a779aaeb0bd448b616398f86560b23f14be53e`.
Production inspected: `taskforge-prod-linux-v40-cluster-control-final-r57.tar(4).gz`.
SHA-256: `4f49e6f2485afe6f139a626f89a56d6f6646bbaff249d3c1e27a3223b2d8f705`.
The production archive is not modified by this stage.

All **1443** original source files are retained. Only these five existing files change:

- `services/tasks/assignment-api/Data/TasksDbContext.cs`
- `services/execution/api/Domain/ExecutionModels.cs`
- `services/execution/api/Data/ExecutionDbContext.cs`
- `services/solutions/api/Domain/SolutionSubmission.cs`
- `services/solutions/api/Data/SolutionsDbContext.cs`

Additional files contain the SQL domain/mappings/guards, design-time factories,
a local EF tool manifest, the model-check executable, source verification scripts
and stage documentation. No original frontend, runner, workflow, Dockerfile or
Compose manifest was edited. `Assignment.cs` is byte-for-byte unchanged.

All **86** original migration/snapshot files are byte-for-byte unchanged, and no
migration or snapshot was created. Baseline hashes are included in this directory.
The source-only guard checks all original files, not only git-tracked changes.

## Completed checks

| Check | Result | Scope / limitation |
| --- | --- | --- |
| SQL migration-boundary guard | PASS | 1443 original files retained; 86 protected files unchanged; source structure only. |
| Existing migration-tooling safety | PASS | Existing source safety invariants. |
| Existing C# source invariants | PASS | Static checks, NOT C# compilation. |
| Docker build-context audit | PASS | 34 image entries; NOT Docker image builds. |
| Workflow/image inventory integrity | PASS | 34 matrix images, Dockerfiles and production image entries. |
| Frontend architecture | PASS | Includes 45 SPA routes; NOT a frontend production build. |
| Frontend cluster Node tests | PASS | 14 tests passed, 0 failed. |
| Existing OJ security script, available sections | COMPLETED | Static architecture, Python policy, JavaScript bootstrap, C sandbox source checks and Go tests/vet. |
| r57 edge lifecycle tests | PASS | 46 tests, using unchanged production sources. |
| r57 targeted integration classes | PASS | 11 tests: PrimaryWaitTests, CertbotContainerTests and PatroniHTTPTests. |
| r57 package SHA-256 manifest | PASS | Original production package files checked against R57_MANIFEST.sha256. |
| Existing-file diff whitespace | PASS | `git diff --check`. |

Logs are in `./qa/`. The available OJ security sections completed on a retry after
initial Go compilation exceeded the tool-call time budget. Its final log contains
`all available checks passed`. The script's generic message saying analyzer
compilation is verified by a Docker build does **not** mean a Docker build was
performed here: neither Docker nor Cargo is available in this environment.

The full r57 release-script invocation exceeded the tool-call time budget during
CertificateShellTests; its partial log is included for transparency. The report
therefore does NOT claim the complete r57 release suite passed. The 46 + 11 tests
listed above were run separately and completed. They use test doubles/local test
servers; they are not live A/B/C failover, Cloudflare or certificate issuance tests.

## Not executed: mandatory local gate before generating migrations

**No .NET SDK is installed in this environment.** Attempts to obtain the official
SDK failed because outbound name resolution/downloads were unavailable. Therefore:

- The modified C# projects have NOT been compiled here.
- The EF Core models have NOT been built/validated here.
- The prepared **43** .NET model/domain checks have NOT been executed here.
- No EF migrations have been generated or applied.
- No SQL worker, database adapter, sandbox isolation, load or end-to-end SQL test
  exists at this migration-boundary stage.
- No Docker/Compose runtime or frontend production build was run here.

Run locally from the extracted source root:

```bash
dotnet --version
dotnet tool restore
dotnet run --project ./tools/sql-domain-check/TaskForge.Sql.DomainCheck.csproj -c Release
```

Stop on any failure and return the output before generating migrations. This
executable references all three modified API projects and builds their EF models
without starting their web applications. Its SaveChanges tests suppress database
writes through an interceptor. It tests model metadata and domain guards, not real
PostgreSQL constraint execution or student SQL execution.

The exact three `dotnet ef migrations add` commands and the return procedure are in
`./MIGRATION_HANDOFF.md`. Do not run `database update` or deploy this stage.
Once user-generated migrations are returned, review their schema/data effects and
run real migration integration tests before exposing any new SQL producer.

## Packaging verification

This is a full source tree, not a patch or an installer that needs an old directory.
The release is checked by extracting the delivered ZIP to a fresh directory,
checking every original file against its baseline, running the included static
guard there, and checking ZIP member CRCs. These checks establish archive
completeness/integrity; they do not establish build or deployment readiness.

The production r57 archive is deliberately not rebranded as r58. A self-contained
new production revision belongs after the migration handoff and runtime work.
