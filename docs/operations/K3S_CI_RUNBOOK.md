# TaskForge K3s: CI/CD release gate

**Target:** `cluster-v2`, `k3s-*` and `k3s/**` only. The workflow is
`.github/workflows/k3s-sandbox-ci.yml` and never deploys to a server.
Its only publish destination is
`ghcr.io/<owner>/taskforge-k3s-lab/<component>:<commit-sha>`.

## Release graph

```mermaid
flowchart LR
  S[Repository and K3s validation] --> D[Seven OCI image builds]
  S --> Y[YugabyteDB schema and persistence]
  A[Six .NET API builds and tests] --> P[Release to isolated GHCR]
  W[React tests and production build] --> P
  D --> P
  Y --> P
```

The jobs for API builds and React run independently from the Kubernetes
preflight. The container build matrix and the database integration test start
once the preflight succeeds. The release matrix starts **only if every required
job succeeds**; the resulting images are tagged by the exact commit SHA.
There are no SSH credentials, deployment secrets, production endpoints, or
`:develop`/`:latest` tags in this workflow.

## YugabyteDB smoke test

`scripts/ci/yugabyte-smoke.sh` owns the disposable CI database lifecycle.
The workflow uses its commands in this order:

| Command | Purpose |
|---|---|
| `start` | Pull pinned image, start an RF=1 Docker container, wait for YSQL connectivity |
| `create` | Run the isolated `taskforge_k3s_*` logical-database bootstrap |
| `migrate` | Apply EF migrations sequentially for six service databases |
| `verify` | Check migration history, domain tables, and real INSERT/SELECT on each database |
| `diagnose` | On failure, capture process status, memory, filtered startup output and internal logs |
| `cleanup` | Always destroy the disposable CI-only container |

**Connection rule:** `yugabyted` running in Docker binds YSQL to its container
hostname/IP, so in-container `ysqlsh` must target that hostname, **not**
`127.0.0.1`. The script starts Docker with an explicit `--hostname` and uses it
for all `docker exec ... ysqlsh` commands. The .NET processes run on the GitHub
runner, outside the container, and therefore connect through the loopback-only
published port `127.0.0.1:15433` instead.

Readiness is bounded to seven minutes, each query is bounded to ten seconds,
and progress is printed periodically. The first failed health check is not
interpreted as a database failure; health checks retry while the container is
running. If the container exits or the deadline is reached, the job fails with
an explicit GitHub annotation and a downloadable `taskforge-ysql-diagnostics`
artifact. A successful container process is not sufficient: `SELECT 1` and
`SELECT version()` must both succeed.

The job uses the same YugabyteDB **2026.1.2** release family as the K3s
Helm chart defaults. It is an integration test of YSQL and EF, not a Kubernetes
installation test. Kubernetes deployment is separately verified on the local PC.

## Failure triage

**`01 | Repository & Kubernetes validation` failed:** open the first failing
step. It prints missing/forbidden paths, Docker context mismatches or a
Kubernetes validation error. Do not disable the branch-isolation check.

**`03 | YugabyteDB - schema & persistence` failed in `start`:** download
`taskforge-ysql-diagnostics`; inspect `summary.txt` for container state,
`oom_killed`, last readiness error, and YB server logs. A spinning Docker
startup banner is not evidence of YSQL readiness. No migration has run.

**Database `create` failed:** inspect SQL/bootstrap permissions and messages;
this affects only the temporary CI database.

**Database `migrate` failed:** expand the first `EF Core | <service>` log group
with an error. Identify the exact migration and unsupported YSQL statement.
Do not mark the job successful or bypass schema verification. The existing
Npgsql migration history override is restricted to single-run migration jobs.

**Database `verify` failed:** read the printed database name and failed assertion.
All six databases must have migration history, application tables and working
INSERT/SELECT, rather than only checking the `yugabyte` bootstrap database.

**`03 | OCI image build` failed:** inspect the Docker build action's build record.
The runtime image build uses the same Dockerfile and context as the local K3s lab.

**`04 | GHCR release` skipped:** one of the required jobs failed or was skipped.
That is an intentional release gate, not an independent failure.

## Local PC (separate from GitHub)

The GitHub Actions build does **not** connect to a home PC, VPS or running
TaskForge instance. For local one-node K3s, follow `README_K3S_BRANCH.md` and
`infrastructure/k8s/taskforge-yugabyte/local/START_LOCAL_RU.md`.
That deployment has persistent PVCs and is intentionally not destroyed by CI.
Its YugabyteDB RF=1 profile is not fault tolerant.

## Evidence and outstanding risks

During the `cluster-v2` run at commit `a01d2ab` (19 November 2026), all six
.NET jobs, React, repository validation, and all seven OCI smoke builds passed.
The original database test did **not** pass: it timed out probing `127.0.0.1`
inside Docker, then dumped an unhelpful startup animation. The script and
mock-based regression tests now correct that probe, but their existence is
**not evidence that a new live YugabyteDB migration run has passed**.

Only an actual subsequent green GitHub Actions run establishes that the selected
YugabyteDB version accepts all six EF migration sets. Local K3s end-to-end
behavior additionally requires a real deployment on the target PC.
