# PostgreSQL version policy

Current default image in both dev and prod compose files is PostgreSQL 18:

```env
POSTGRES_IMAGE=postgres:18-alpine
```

The image is still configurable through `.env`, but the repository default is no longer 16 or 17.
This keeps the new microservice stack on the newest planned database baseline instead of lagging behind the legacy monolith.

## Why PostgreSQL 18 is the default now

Pros:

- one current major baseline for dev, staging, and prod;
- longer support window than older major versions;
- avoids the confusing situation where the newer microservice project used an older database than the legacy monolith;
- makes PostgreSQL 18 compatibility issues visible early, while the project is still being actively reshaped.

Cons / risks:

- an existing PostgreSQL 16/17 data directory cannot be safely reused just by changing the Docker image tag;
- major upgrades require a real migration path: dump/restore, `pg_upgrade`, or logical replication;
- query plans, collations, extensions, and driver edge cases should be smoke-tested before production rollout.

## Fresh development database

For a disposable dev environment, the simplest path is to recreate the dev volume:

```bash
cd deploy/dev
./compose.sh down
./compose.sh up -d postgres
```

If the existing dev volume was created by PostgreSQL 16 or 17 and you do not need its data, remove that volume before starting PostgreSQL 18.

## Existing production/staging database

Do **not** point `postgres:18-alpine` at an existing 16/17 volume blindly.
Use one of these controlled paths:

1. backup with `pg_dumpall` or per-database `pg_dump`;
2. start a clean PostgreSQL 18 volume;
3. restore the backup;
4. run EF migrations;
5. run smoke tests for auth, assignments, solutions, execution, quiz, files, AI and bots;
6. only then switch traffic.

For large databases, prefer a tested `pg_upgrade` or logical replication plan.

## Docker volume mount for PostgreSQL 18

PostgreSQL 18 Docker images should mount the data volume at:

```yaml
postgres-data:/var/lib/postgresql
```

Do not mount the volume at `/var/lib/postgresql/data`. The 18+ image layout stores data below a major-version-specific directory and intentionally rejects old `/var/lib/postgresql/data` mounts that look like unsafe direct upgrades.

Both dev and prod compose files must use the same mount target.
