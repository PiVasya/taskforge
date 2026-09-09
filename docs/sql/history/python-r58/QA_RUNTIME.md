# SQL runtime QA: actual results and open release gates

Date: 2026-09-09. Source baseline: the user's develop(212). Production baseline:
standalone v40-r57. Status: implementation candidate, NOT production-certified.

## Migration and model preservation

All 92 migration/snapshot files match the user archive byte-for-byte, including the
three AddSqlDomain migrations and their generated designers/snapshots. Another 104
Domain/Data files are unchanged. There are no new migrations, no hand-edited
snapshots and no Entity/DbContext change in this runtime stage. The original source
files remain in the complete source archive. The hash verification is reproducible
with `python3 ./scripts/ci/check-sql-runtime.py` from the source root.

## Executed in this environment

| Check | Actual result and scope |
| --- | --- |
| SQL Python unit + real SQLite restricted subprocess suite | 40 discovered; 33 passed; 7 PostgreSQL/MySQL cases explicitly skipped |
| Concurrent SQLite workload | Included above: 100 Run and 100 Check callers through 4 execution slots; not 100 simultaneous DB processes or an HTTP-system load test |
| SQL frontend model tests | 6 passed |
| Existing frontend cluster tests | 14 passed |
| Changed/new JS/JSX syntax | 16 files parsed/transpiled with TypeScript; not a production React build |
| Source structural gates | SQL preservation/routing, C# source invariants, workflow/image alignment, Docker context copies, migration tooling safety and EF dependency declarations passed |
| Image inventory | 35 workflow images, 35 Dockerfiles, 35 production image entries aligned |
| Frontend architecture checks | Passed; SPA/Caddy invariants include 45 routes |
| Available existing OJ security checks | Static policy, Python/JavaScript/C checks, Go tests/vet passed; Rust compiler was unavailable and was not reported as passing |
| Standalone r58 offline suite | Passed baseline storage/cluster assertions plus 46 lifecycle tests, 16 integration/mocked-process tests and 8 SQL-specific topology/HA tests |
| SQL-specific r58 checks | Full/lite assignment, optional readiness, deferred SQL start/stop, private networks, tmpfs/resource/privilege declarations, lite preparation, preserved credentials/pins and self-containment |

SQLite coverage executes result/state/schema comparisons, correct/wrong/NULL cases,
DML/DDL/index changes, constraint names/collations/SQLite table options, source and
result limits, timeout, file/administration restrictions, independent attempts,
expected/dataset cache identity, unchanged GOLDEN, quarantine and nonblocking LRU
cleanup. These tests do not stand in for actual PostgreSQL or MySQL behavior.

The production tests do not call live production Docker, Cloudflare or ACME. Some
integration cases use local test HTTP servers, openssl and mocked external commands.
They establish offline control-plane regressions, not a successful real A/B failover.

## Not executed; still mandatory before rollout

.NET SDK and Docker are absent in the editing environment. The new C# APIs and
Education service have NOT been compiled here. The user's earlier successful 44
stage-1 domain checks are historical evidence for that stage, not proof that these
new APIs compile or pass runtime integration. There was no new NuGet restore/EF
model build, migration-application rehearsal or SQL HTTP end-to-end test here.

The full npm/CRA production build and browser/editor E2E were NOT executed here.
The available Node tests and JSX parser do not check imported package integration,
backend/frontend contract compatibility or real browser interactions.

The PostgreSQL and MySQL adapter integration tests are implemented in
`scripts/sql/test-engines.sh`, but were NOT executed here. Their container entrypoint,
non-root production Compose deployment, actual resource settings, security behavior,
restart/cleanup and runtime fingerprints require a Docker-capable environment.
The script exercises dedicated test containers; a passing adapter gate still does
not replace the production Compose/dev-stack acceptance test.

No images were built or pushed to GHCR. No real database was migrated by this
session. r58 was not installed on A/B/C. There was no live Patroni/Cloudflare/HTTPS
failover measurement, real HTTP 100-Run/100-Check load test or public-host isolation
certification. Existing grading/rating/analytics flows require the stated end-to-end
regression checks after successful builds.

Run from the extracted source root:

```bash
bash ./scripts/check-sql-update.sh
bash ./scripts/sql/test-engines.sh
```

Then complete the dev-stack and staging-cluster acceptance checklist in
DEPLOYMENT.md. These scripts do not create more migrations. The workflows gate
image builds on their success; do not bypass a failed gate. No unconditional claim
of "all original handoff readiness criteria passed" is justified by this report.

## Scope limitations

SQL Server/MariaDB, full-instance administration/COW providers, persistent SQL
sessions, active-active A+B execution, engine sharding and a SQL metrics dashboard
are not delivered. Normal shared-server isolation has a whole-engine resource/failure
boundary, not per-database cgroups. C stays lite; making C Primary does not provide
SQL execution on C. Exact-profile changes require explicit validation/publication.
Unsupported advanced schema objects are rejected, not silently judged equivalent.

## Reproduction evidence

The source contains raw logs under docs/sql/qa/runtime/ for the executed Python,
frontend/source gates, existing available OJ checks and r58 offline suite. Release
packaging additionally verifies the extracted file sets and SHA256 hashes, preserves
executable permissions and checks that no old release directory is required.
