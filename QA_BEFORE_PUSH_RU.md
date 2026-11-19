# Pre-push quality gate: TaskForge K3s / YugabyteDB

## Scope

Experimental `cluster-v2` branch only. No production deployment, databases,
secrets or `develop` images are modified by CI. `services/ai/api` retains the
account analyzer source; the retired LLM worker and independent CT/quiz services
remain absent. The local `core` profile deploys six API services, not all of
TaskForge's optional integrations or code runners.

## Incident: YugabyteDB CI timeout, 19 November 2026

**Evidence:** GitHub Actions run `44450032203`, commit `a01d2ab1`.
The repository gate, all six .NET API builds/tests, React build/tests and
all seven OCI build validations passed. The schema integration job failed
in database readiness; migrations did not start, so image publication was
correctly blocked.

**Cause:** Docker `yugabyted` was started with no fixed hostname, while every
`docker exec ... ysqlsh` command connected to `127.0.0.1`. In this Docker
configuration YSQL listens on the container's hostname/network interface.
The readiness loop retried for six minutes without a useful error and finally
printed the YugabyteDB startup spinner. `YugabyteDB Started` was not proof of
SQL connectivity. Refer to the vendor Docker quick-start documentation.

**Changes:**

1. Start the CI container with an explicit hostname, use that hostname in all
   in-container SQL checks, and retain loopback-only host port publication for .NET.
2. Isolate the database lifecycle in `scripts/ci/yugabyte-smoke.sh`.
3. Add time-bounded YSQL readiness, container crash checks, OOM/process status,
   and a downloadable diagnostic artifact on failure.
4. Check migration history, tables and read/write operations independently in
   all six service databases.
5. Add mock-based regressions for success, failure, isolation and cleanup.
6. Normalize CI names and job summaries while keeping the same release-gate DAG.
7. Pin GitHub Actions runners to Ubuntu 24.04 and use Node 24-based setup actions
   to avoid relying on mutable `ubuntu-latest` runner images and deprecated Node 20 actions.

## Verification matrix

| Validation | Evidence |
|---|---|
| Six .NET API builds/tests | Passed in GitHub Actions run `44450032203` (before this change) |
| React production build and test suite | Passed in the same GitHub Actions run |
| Seven OCI runtime image builds | Passed in the same GitHub Actions run |
| K3s branch layout, Docker context and EF migration contract | Local regression scripts |
| New disposable YSQL CI lifecycle and failure diagnostics | Mock-based regression scripts, no real DB |
| Complete GitHub Actions YAML and release dependency graph | Static validation |
| Live migrations against YugabyteDB after this correction | **Not yet verified; next GitHub Actions run required** |
| Local K3s deployment on a PC | **Not yet verified** |

## Release policy

A failed YSQL schema test or runtime image build blocks GHCR publication.
Do not bypass it to obtain green checks. If the next run fails during EF
migration, the failing database/service and SQL statement must be investigated
with the new diagnostics, and the underlying incompatibility fixed before
proceeding. `docs/operations/K3S_CI_RUNBOOK.md` contains the triage guide.
