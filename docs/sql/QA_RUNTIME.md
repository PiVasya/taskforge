# SQL runtime: factual verification report

Date: 2026-09-14. Scope: the SQL profile-compatibility update based on the
user-provided develop(218) source tree. This report describes checks actually
executed in the current environment; it is not a production-rollout certificate.

Current-boundary note (2026-09-23): after this historical QA run, the user explicitly
generated `RemoveAssignmentDifficulty` and `RemoveQuizDifficulty`. The active
source guard now freezes 96 migration/snapshot files plus the same 104 protected
Domain/Data files. The 92-file figures below remain the factual result of the
2026-09-14 run and are intentionally not rewritten.

## Preservation and architectural boundaries

- All 92 protected migration/designer/snapshot files and all 104 protected
  Domain/Data files still match the recorded user hash list byte-for-byte.
  No migration was generated, edited or applied.
- The production SQL executor remains the Go + CGO runtime. Python execution was
  not reintroduced.
- Worker build identity is diagnostic only. It is not part of newly registered
  immutable SQL engine profile identity.
- New profiles carry an explicit `executionSemanticsVersion`, an engine-local
  native client version/digest, and the existing engine/runtime settings that can
  affect SQL behavior. A PostgreSQL client change does not mutate MySQL or SQLite
  profile identity, and vice versa.
- SQLite no longer uses `sha256(executorFingerprint)` as its runtime identity.
  Its runtime digest is derived from the SQLite component identity and sorted
  SQLite compile options.
- Historical profiles remain immutable. Compatibility is represented separately
  and is granted only by `SqlProfileCompatibility`; profiles are never rewritten
  or matched merely by engine name.
- The legacy Go bridge is deliberately narrow. For SQLite it accepts the old
  build-bound runtime digest only when it exactly equals
  `sha256(executorFingerprint)`, proving the known historical profile shape.
- The ordinary editor groups immutable history into the three logical engines
  while retaining an already selected historical profile. Moving an actually
  incompatible historical target to the current runtime is an explicit action.
- The production control package remains r64. This application/runtime fix does
  not alter cluster migration, storage, Primary-election, Cloudflare or C=lite
  behavior.

## Checks executed here

| Gate | Result |
| --- | --- |
| Protected-source/runtime structural guard | PASS: 92 migration/snapshot + 104 Domain/Data files unchanged; SQL runtime/Compose/CI invariants pass |
| Go unit/package tests | PASS: `go test -vet=off ./...` |
| Go vet | PASS: `go vet ./...` |
| Go race tests | PASS: `go test -race -count=1 ./...` |
| Release-style Go binary | PASS: stripped binary builds and reports the expected Go/native runtime versions |
| Native isolation self-test | PASS: `SQL_ALL_THREAD_ISOLATION_OK` |
| Local SQLite load regression | PASS: 100 Run + 100 Check (200 executions) under the race detector |
| Web SQL model tests | PASS: 11/11 |
| Web architecture guard | PASS: 45 SPA/Caddy routes and frontend architecture invariants |
| r64 SQL control-package tests | PASS: 10/10 `tests/test_sql_runtime.py` |
| r64 base release regression | PASS before the broader integration suite reached an environment timeout |

The complete `tests/test-r64-release.sh` invocation was also started. Its manifest,
syntax, topology, state-import, volume-safety, Primary-switch, operability, 46 edge
lifecycle tests and earlier base regression stages passed. The command later hit
the execution-environment timeout while running the broader certificate integration
suite. That timeout is not recorded as a pass, and the script was not weakened or
silently skipped.

## Gates not executable in this environment

- .NET SDK/compiler is unavailable here, so the updated C# API/domain code and the
  new `SqlProfileCompatibility` domain regression cases were not compiled locally.
  They must pass the normal .NET/CI gate before image publication.
- A full frontend production build was not run because the complete installed npm
  dependency tree is not available in this extracted workspace. The repository's
  dependency-free SQL model tests and architecture checker did run.
- Docker image build and real PostgreSQL/MySQL/RabbitMQ integration were not run
  here. No production containers, databases, Cloudflare records or certificates
  were modified.

## Required release gates

From the source root, in the normal build/CI environment with its documented
prerequisites:

```bash
bash ./scripts/check-sql-update.sh
bash ./scripts/sql/test-engines.sh
```

Then build/publish the normal application images together (`tasks` API,
`execution` API, `sql-worker`, and web). During a rolling update, updating the
Tasks/Execution APIs before relying on compatibility aliases gives the new worker
the complete alias contract. An older Tasks API is still accepted by the new
worker in exact-profile mode, so a mixed-version window fails closed instead of
guessing compatibility.
