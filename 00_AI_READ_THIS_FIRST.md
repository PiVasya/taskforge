# TaskForge — isolated K3s source

This is an independent local-development Kubernetes branch with a **new YugabyteDB
YSQL database**. It does not migrate the live production environment or its data.
Do not reintroduce retired production/dev Compose deployment scripts or depend on
old `develop` workflows. Keep runtime source, docs and security controls for the
local K3s lab. No automatic remote deployment.

## Data ownership

- **Education** owns course maps, groups, access and education structure.
- **Tasks** owns all five assignment definitions, typed grading specifications,
  SQL datasets and test/math grading. It **must not store user attempts**.
- **Solutions** owns user attempts for code, SQL, image, test and math; reviews,
  quotas, leaderboard, rating and one durable admin solution event feed.
- **Execution** owns technical execution jobs, not submission history.

There is no Ownership V2 relay, backfill, shadow attempt table or duplicate
owner. The K3s namespace, databases and credentials are isolated.

## Required migration work before first local startup

From the repository root on a machine with .NET 10 SDK:

```bash
bash scripts/generate-migrations.sh ConsolidateAttempts tasks,solutions
```

This scaffolds two EF migration pairs; it **does not apply SQL**. Review them,
run `python3 scripts/ci/check-database-table-ownership.py` and .NET tests, then
run the disposable YugabyteDB CI gate before deploying the local cluster.
`build-images.sh` and `run-local.sh` refuse to proceed when EF ownership
snapshots are not ready. Education needs no new ownership migration.

Never hand-edit generated active EF migrations or ModelSnapshot files as a
substitute for EF generation. The canceled, unapplied sandbox staging migration
pairs may be removed together with their snapshots; applied migration history
must never be rewritten. The pinned dotnet-ef CLI is in `.config/dotnet-tools.json`.

## Security and compatibility rules

- Do not restore the retired LLM/agent/assistant UI, worker or providers.
  Keep deterministic account analysis independent of browser automation.
- Keep SQL student execution in the Go/CGO sandbox and never run submitted SQL
  against TaskForge service databases. Immutable SQL runtime profiles must not
  change identity on rebuild.
- User-submitted code must pass code-analyzer and exact-source RSA attestation
  before any runner execution; fail closed. Preserve per-runner resource isolation,
  process cleanup, security policy and public/private key separation.
- Browser API must reject arbitrary URLs, protect writable sessions with a
  user access token, enforce same-origin restrictions and use controlled routes.
- Role ranks: SuperAdmin > Admin > Editor/LearningEditor > Minecraft > User.
  Only strictly lower roles may be granted or revoked; SuperAdmin cannot be
  assigned via the public administrative API.
- Public JSON graph exports use schema v5. Keep typed definition fields
  (`code-test`, `sql-test`, `image-test`, `test`, `math`) and SQL datasets;
  private verification data must be emitted only in the checks scope.
- Keep Minecraft item/death recovery fail-closed on unknown or unlinked
  accounts; preserve idempotent charges, exact UUID ownership, single-choice
  offers and user-level shared balances. Keep only the supported plugins.
- Do not log tokens, raw link codes, passwords, user-submitted private answers
  or full item payloads. Retain useful request and failure diagnostics.

## Tests and scope

- Behavior tests must exercise production implementation, not grep specific
  method names. Structural checks are allowed for ownership, Docker, workflow
  and migration boundaries; they are not evidence of runtime correctness.
- `scripts/tests/repository.sh` checks source/CI architecture and mocks;
  `scripts/tests/fast.sh` adds .NET, frontend and Go;
  `scripts/tests/all.sh` adds disposable YugabyteDB. A successful mock test
  does **not** prove real YSQL or K3s end-to-end behavior.
- Keep source security checks and real integration tests where applicable.
  The `core` K3s profile does not deploy language runner containers; their
  sandbox security must be verified separately before enabling them.
- Do not add release notes, historical stage summaries or extra installation
  wrappers to this root. User-authored `Планы.txt` is not a generated artifact.

See `docs/README.md`, `docs/architecture/TASK_DATA_OWNERSHIP.md`,
`docs/operations/README_K3S_BRANCH.md` and `docs/testing/README.md`.
