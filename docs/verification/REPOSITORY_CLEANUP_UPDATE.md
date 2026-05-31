# Repository cleanup update

This update aligns the repository with the new migration policy where EF migrations are committed in `develop`.

## Changed

- Added root `.dockerignore` so Docker build contexts do not include local logs, `bin/obj`, `node_modules`, archives, dumps, local env files, Python caches, Go/Rust/Java artifacts and temporary data.
- Replaced `scripts/generate-migrations.sh` with a safe version that checks `dotnet ef migrations has-pending-model-changes` before creating a migration.
- Moved the old unconditional behavior to `scripts/generate-migrations-force.sh`.
- Removed old `MIGRATIONS_REQUIRED.md` marker files because migrations now exist and are tracked.
- Updated migration documentation in `README.md`, `deploy/prod/docs/MIGRATIONS_POLICY.md`, `docs/architecture/MIGRATIONS_INDEX.md`, `docs/architecture/REAL_MICROSERVICES.md`, and related verification docs.
- Updated `scripts/verify-structure.sh` to validate migration directories and snapshots instead of requiring migrations to be absent.

## Current policy

- DB-owning services must have `Migrations/` and exactly one `*ModelSnapshot.cs`.
- Empty migration `Up()` bodies fail verification.
- Generated migrations are source files and should be committed when intentionally created.
- `MIGRATIONS_REQUIRED.md` files are not used anymore.

## Verification

`./scripts/verify-structure.sh` passed after this update.
