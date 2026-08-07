# Browser / AI API audit

This audit compares the source archive immediately before the Browser/AI API change with the first Browser/AI API archive and records the corrections applied before release.

## Database migrations

The migration trees were compared by path and SHA-256, including every file under `Migrations` and every `*ModelSnapshot.cs` file.

- Baseline migration/snapshot files checked: **80**.
- Baseline vs first Browser/AI API archive: **identical**.
- Baseline vs audited Browser/AI API archive: **identical**.
- No migration or model snapshot was generated, edited, deleted, or renamed by this update.

The identity model intentionally has one pending schema change: `IdentityUser.AccountType`. Generate only the identity migration after reviewing the code:

```bash
./scripts/generate-migrations.sh AddAiAccountType identity
```

The migration helper now requires an explicit target. Calling it with only a migration name exits with code `2` and generates nothing. `all` is an explicit opt-in only.

## Project AI rules

`00_AI_READ_THIS_FIRST.md` is byte-for-byte identical to the baseline. The audited tree preserves the relevant invariants:

- development debug logging defaults stay enabled;
- existing migrations and snapshots are untouched;
- assignment JSON import/export implementation is untouched;
- Minecraft plugin implementation is untouched;
- OJ analyzer, execution runners and solution execution paths are untouched;
- AI account type is a marker independent from authorization role;
- no special AI privilege or hidden AI role is introduced;
- anonymous browser inspection is read-only;
- Browser API accepts configured TaskForge origins plus relative paths, not arbitrary target URLs;
- public browser endpoints are guarded by gateway limits, application/Redis limits, Chromium capacity limits and session TTLs.

## Corrections made during this audit

The first Browser/AI archive accidentally carried an unrelated Account Intelligence V2 change set. Those files were restored exactly from the baseline and `ACCOUNT_INTELLIGENCE_V2_UPDATE.md` was removed from the audited tree.

Browser API authenticated calls now accept only an explicit `Authorization: Bearer <access token>`. Ambient `tf_at` cookies are ignored by Browser API endpoints so an ordinary human browser session cannot be converted into an authenticated headless-browser session by cross-origin requests. Chromium itself still receives the validated access token when an authenticated Browser API caller requests a TaskForge page.

The migration helpers were hardened so they cannot sweep every DbContext by default. A CI guard (`scripts/ci/check-migration-tooling-safety.py`) checks this invariant, and Browser API security checks assert explicit Bearer-only API authentication.

## Verification performed

The audited tree passed the available static/runtime-independent checks:

```text
scripts/security/check-browser-api-security.sh
scripts/ci/check-migration-tooling-safety.py
scripts/ci/check-workflow-integrity.py
scripts/verify-runtime-config.sh
scripts/ci/check-minecraft-link-invariants.sh
scripts/ci/check-minecraft-plugin-runtime-invariants.sh
scripts/security/check-oj-security.sh
```

Shell syntax, YAML/JSON parsing and whitespace checks also passed. The current audit environment does not contain the .NET SDK or Docker, so `dotnet build`, Docker image builds, Compose runtime startup and a real Chromium smoke test must run in CI/server infrastructure.
