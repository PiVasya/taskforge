# EF Core migrations — K3s local branch

The service migrations are stored under `services/*/api/Migrations/` (Tasks:
`services/tasks/assignment-api/Migrations/`). The pinned CLI is invoked via
`scripts/generate-migrations.sh`. Do not hand-edit generated migrations or
model snapshots.

For the fresh K3s lab, the canceled staging migration pairs were removed
before the database was initialized. Two migrations still need to be generated:

```bash
bash scripts/generate-migrations.sh ConsolidateAttempts tasks,solutions
```

Tasks must **not** own attempts; Solutions must own test/math `TaskAttempts`
alongside code/SQL/image submissions. Education owns course structure; Tasks
owns task definitions.

Check model-vs-snapshot drift after generation with:

```bash
python3 scripts/ci/check-database-table-ownership.py
python3 scripts/ci/check-ef-migrations.py --targets assignment,solutions,education
```

These are source checks, not YugabyteDB DDL execution. CI runs EF migrations
on an isolated disposable YugabyteDB separately.
