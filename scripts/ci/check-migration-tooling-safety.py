#!/usr/bin/env python3
"""Static guard for TaskForge migration helper scripts.

The project rule is that migrations are generated only when the developer asks for
one, and a helper command must not sweep every DbContext by default. This check
keeps that invariant enforceable in CI without generating or editing a migration.
"""
from __future__ import annotations

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]


def fail(message: str) -> None:
    raise SystemExit(f"migration tooling safety check failed: {message}")


def read(relative: str) -> str:
    path = ROOT / relative
    if not path.is_file():
        fail(f"missing {relative}")
    return path.read_text(encoding="utf-8")


normal = read("scripts/generate-migrations.sh")
force = read("scripts/generate-migrations-force.sh")
checker = read("scripts/ci/check-ef-migrations.py")
sql_runtime_guard = read("scripts/ci/check-sql-runtime.py")

for name, script in (("normal", normal), ("force", force)):
    if 'TARGET_SPEC="${2:-}"' not in script:
        fail(f"{name} generator no longer requires an explicit second target argument")
    if re.search(r'MIGRATION_NAME="\$\{1:-[^}]', script):
        fail(f"{name} generator silently invents a migration name")
    if 'if [[ -z "$MIGRATION_NAME" || -z "$TARGET_SPEC" ]]' not in script:
        fail(f"{name} generator does not fail closed when the target is omitted")
    if 'if [[ "$TARGET_SPEC" == "all" ]]' not in script:
        fail(f"{name} generator has no explicit opt-in path for all contexts")
    if 'SELECTED=("${ITEMS[@]}")' not in script:
        fail(f"{name} generator all-context mode is no longer visibly explicit")

if 'has-pending-model-changes' not in normal:
    fail("normal generator no longer checks EF pending model changes")
if 'dbcontext info' not in normal:
    fail("normal generator no longer probes the selected DbContext before interpreting EF output")
if 'No migration was generated' not in normal:
    fail("normal generator no longer documents fail-closed behavior for ambiguous EF failures")
if 'identity|services/identity/api/TaskForge.Identity.Api.csproj|IdentityDbContext|Migrations' not in normal:
    fail("identity target mapping is missing")
if 'identity|services/identity/api/TaskForge.Identity.Api.csproj|IdentityDbContext|Migrations' not in force:
    fail("identity target mapping is missing from force generator")

# The force helper must be visibly dangerous and must never be called by the normal helper.
if 'generate-migrations-force.sh' in normal and 'Use ./scripts/generate-migrations-force.sh' not in normal:
    fail("normal generator appears to invoke force migration generation")
if 'dotnet ef migrations add' not in force:
    fail("force generator unexpectedly lost its only explicit migration action")


# The obsolete date-pinned checksum baseline must stay retired. Legitimate migrations
# change model/snapshot bytes by design; EF semantic drift is the source of truth.
if (ROOT / "docs/sql/user-20260923-protected.sha256").exists():
    fail("obsolete user-20260923-protected.sha256 checksum manifest must not be restored")
if "protected.sha256" in sql_runtime_guard:
    fail("SQL runtime structural guard must not freeze EF models/migrations by checksum")

# CI must validate model/migration drift semantically through EF, not by freezing file hashes.
if 'migrations has-pending-model-changes' not in checker:
    fail("CI migration checker no longer asks EF for pending model changes")
if 'dotnet", "tool", "restore' not in checker:
    fail("CI migration checker no longer restores the pinned dotnet-ef tool")
if 'dbcontext", "info' not in checker:
    fail("CI migration checker no longer probes DbContext design-time construction")
if 'migrations add' in checker or 'database update' in checker:
    fail("CI migration checker must never create or apply migrations")
if 'GENERATOR = ROOT / "scripts/generate-migrations.sh"' not in checker:
    fail("CI migration checker must use generate-migrations.sh as the target source of truth")

print("migration tooling safety invariants ok")
