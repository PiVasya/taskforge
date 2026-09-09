# SQL migration boundary: EF runtime dependency correction

Date: 2026-09-09. Package: `taskforge-develop-sql-domain-stage1-fix1`.
This is a self-contained source handoff, not a deployable SQL runtime release.

## Evidence and diagnosis

The user's .NET SDK 10.0.111 run restored dotnet-ef 10.0.8, compiled the API
projects against EF Core 10.0.8, then failed building the model-check with CS1705.
Its MSB3277 output selected EF Core, Relational and Abstractions 10.0.4 for the
consumer while API assemblies referenced 10.0.8.

Inspection of the actual stage-1 csproj files found only a private Design 10.0.8
reference and Npgsql.EntityFrameworkCore.PostgreSQL 10.0.2. There were no explicit
EF runtime pins. The provider's package dependencies allow EF 10.0.4 and above;
private Design dependencies do not pin the consuming ProjectReference graph.

The three API projects now explicitly reference EF Core **and Relational 10.0.8**.
Design remains `PrivateAssets=all`, the provider stays 10.0.2 and the local EF CLI
stays 10.0.8. No warning suppression, provider downgrade, snapshot modification or
migration fabrication is used. References remain inside each csproj so the existing
Docker restore layers do not acquire a dependency on an un-copied root props file.

## Changes relative to the first stage-1 source archive

- Three API csproj files: public Core/Relational 10.0.8 references.
- Model-check: one additional runtime assembly-version regression check (44 total).
- An offline source/dependency guard with a mode that inspects real NuGet restore
  output; 19 Python regression tests cover this guard using synthetic fixtures.
- The local gate restores, inspects the four package graphs, builds with MSB3277
  treated as an error, then runs the executable. It does not generate migrations.
- Migration-boundary guard supports explicit documentation relocations and enforces
  the root Markdown rule. Its post-migration mode now rejects missing migration
  pairs or unchanged target snapshots instead of allowing an empty handoff.
- README moved to `docs/README.md`; eight historical root reports moved to
  `docs/history/`; migration instructions moved to `docs/sql/MIGRATION_HANDOFF.md`.
  Root Markdown is only `00_AI_READ_THIS_FIRST.md`.

There are **no Entity/DbContext changes relative to stage 1** in this correction.
The original 1443 source files are retained, including nine documented relocations.
All 86 original migration/snapshot files are unchanged; no migration was added.
Baseline SHA-256 files themselves are preserved unchanged.

## Checks actually completed for this correction

| Check | Result | Scope |
| --- | --- | --- |
| Migration-boundary source guard | PASS | Original files, protected migration bytes, domain invariants and root Markdown policy. |
| Source EF dependency guard | PASS | Three public runtime pins, private Design packages, matched CLI and project references. |
| Dependency guard regression tests | PASS: 19 tests | Synthetic XML/assets fixtures, including rejection of the reported 10.0.4 consumer graph; NOT a NuGet restore or C# build. |
| Missing user migrations, post-migration mode | Rejected as expected | Negative test; no fake migration files were created. |
| Existing migration-tooling safety | PASS | Static safety rules. |
| Existing C# source invariants | PASS | Static only, not compilation. |
| Docker build-context audit | PASS | File/context consistency, not Docker builds. |
| Workflow/image inventory audit | PASS | Existing image/workflow consistency. |
| Frontend architecture check | PASS | Existing architecture check. |
| Frontend cluster Node tests | PASS: 14 tests | Unmodified frontend test suite. |
| Shell syntax | PASS | `bash -n scripts/check-sql-domain.sh`. |
| OJ security script, available sections | PASS | Static rules, Python/JavaScript checks, C source checks, available Go tests and vet. |

Current logs are in `./qa/ef-runtime-fix/`. Previous stage logs remain historical.
The OJ script's generic message about Docker verifying Rust is not evidence of a
Docker build here: neither Cargo nor Docker was available. No Rust build is claimed.

## Checks NOT performed here

.NET SDK, Docker and Cargo are unavailable in the assistant's container. Both DNS
and direct outbound network access failed, so an SDK/real NuGet restore could not be
obtained. The local gate correctly stopped with exit 2 instead of reporting success.

Consequently, the corrected C# projects and the 44 executable model/domain checks
have NOT run here. Real restored dependency graphs have NOT been verified here;
only the source declarations and regression fixtures were checked. The user's
previous log does not establish success of this corrected archive.

No EF migration was generated/applied, no PostgreSQL constraint test was run, and
no SQL worker/adapter/pool/UI/production integration is claimed. Production r57,
including A/B full and C lite, is unchanged and is not rebranded as a new revision.

## Next gate

From the freshly extracted source root:

```bash
bash ./scripts/check-sql-domain.sh
```

Stop and return the log on any error. Only after success, run the three explicit
`dotnet ef migrations add` commands in `./MIGRATION_HANDOFF.md`. Do not run
`dotnet ef database update`, `build.sh` or production deployment for this stage.
Return the complete source with the three user-generated migration pairs and
updated snapshots for review before the SQL runtime work continues.
