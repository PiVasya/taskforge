#!/usr/bin/env python3
"""Read-only EF migration history consistency checks, before EF CLI execution.

This check never writes, generates, applies or removes migration files. It rejects
(1) multiple CREATE TABLE operations without an intervening DROP TABLE and
(2) a ModelSnapshot that disagrees with the newest migration's Designer model.
The second check prevents EF from scaffolding the same model change twice when
someone committed a migration without its updated ModelSnapshot.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

_MIGRATION_FILE = re.compile(r"^\d{14}_.+\.cs$")
_TABLE_OP = re.compile(
    r'migrationBuilder\.(CreateTable|DropTable)\s*\(\s*name\s*:\s*"([^"]+)"',
    re.DOTALL,
)
_METHOD_END = re.compile(r"protected\s+override\s+void\s+Down\s*\(")
_MODEL_BEGIN = "#pragma warning disable 612, 618"
_MODEL_END = "#pragma warning restore 612, 618"
_ENTITY = re.compile(r'modelBuilder\.Entity\s*\(\s*"([^"]+)"')


def model_body(content: str) -> str | None:
    if _MODEL_BEGIN not in content or _MODEL_END not in content:
        return None
    return content.split(_MODEL_BEGIN, 1)[1].split(_MODEL_END, 1)[0].strip()


def inspect_migrations(folder: Path) -> list[str]:
    """Return actionable errors; never modify source or the database."""
    if not folder.is_dir():
        return [f"EF migration directory not found: {folder}"]
    migrations = sorted(
        f for f in folder.iterdir() if f.is_file() and _MIGRATION_FILE.match(f.name)
    )
    migration_files = [f for f in migrations if not f.name.endswith(".Designer.cs")]
    errors: list[str] = []
    created: dict[str, str] = {}
    for f in migration_files:
        text = f.read_text(encoding="utf-8-sig")
        # Never look at Down(): every ordinary migration drops its own tables there.
        up = _METHOD_END.split(text, maxsplit=1)[0]
        for operation, table in _TABLE_OP.findall(up):
            if operation == "DropTable":
                created.pop(table, None)
            elif table in created:
                errors.append(
                    f"duplicate CREATE TABLE {table!r}: {created[table]} and {f.name}. "
                    "Review migration history; do not add another migration or drop production tables."
                )
            else:
                created[table] = f.name

    if migration_files:
        newest = migration_files[-1]
        designer = newest.with_name(newest.stem + ".Designer.cs")
        snapshots = list(folder.glob("*ModelSnapshot.cs"))
        if not designer.is_file():
            errors.append(f"missing migration designer: {designer.name}")
        elif len(snapshots) != 1:
            errors.append(f"expected one ModelSnapshot in {folder}, found {len(snapshots)}")
        else:
            designer_body = model_body(designer.read_text(encoding="utf-8-sig"))
            snapshot_body = model_body(snapshots[0].read_text(encoding="utf-8-sig"))
            if designer_body is None or snapshot_body is None:
                errors.append(f"cannot compare generated EF model blocks: {designer.name}, {snapshots[0].name}")
            elif designer_body != snapshot_body:
                missing = sorted(set(_ENTITY.findall(designer_body)) - set(_ENTITY.findall(snapshot_body)))
                added = sorted(set(_ENTITY.findall(snapshot_body)) - set(_ENTITY.findall(designer_body)))
                diagnostic = ""
                if missing:
                    diagnostic += f" missing-in-snapshot={missing}"
                if added:
                    diagnostic += f" extra-in-snapshot={added}"
                errors.append(
                    f"stale/inconsistent {snapshots[0].name} versus latest {designer.name}."
                    f"{diagnostic} Restore the user-generated matching snapshot, "
                    "do NOT scaffold another migration for the same model change."
                )
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", type=Path, required=True, help="One EF Migrations directory")
    args = parser.parse_args(argv)
    folder = args.dir.resolve()
    errors = inspect_migrations(folder)
    if errors:
        for error in errors:
            print(f"EF MIGRATION HISTORY ERROR: {error}", file=sys.stderr)
        return 1
    print(f"EF MIGRATION HISTORY PASS: {folder}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
