#!/usr/bin/env python3
"""Classify changed paths for TaskForge's SQL-specific CI gates.

The normal frontend/.NET suites already cover SQL UI and ordinary API compilation.
This classifier is intentionally narrow so the expensive provider gate only runs
when the isolated SQL execution runtime/provider harness itself changed.
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import PurePosixPath


CONTRACT_PREFIXES = (
    "services/shared/Sql/",
    "services/tasks/assignment-api/Data/Sql/",
    "services/tasks/assignment-api/Domain/Sql/",
    "services/tasks/assignment-api/Endpoints/Sql/",
    "services/tasks/assignment-api/Services/Sql/",
    "services/solutions/api/Endpoints/Sql/",
    "services/solutions/api/Services/Sql/",
    "services/execution/api/Endpoints/Sql/",
    "services/execution/api/Services/Sql/",
    "tools/sql-domain-check/",
)

CONTRACT_FILES = {
    "scripts/check-sql-domain.sh",
    "scripts/ci/check-sql-runtime.py",
    "scripts/ci/check-sql-ef-dependencies.py",
    "docs/sql/user-212-protected.sha256",
    "docs/sql/user-20260923-protected.sha256",
    "docs/sql/user-212-files.json",
}

ENGINE_PREFIXES = (
    "services/execution/sql-worker/",
    "infrastructure/sql/",
    "scripts/sql/",
)

ENGINE_FILES = {
    "scripts/check-sql-go.sh",
    "deploy/dev/compose/35-sql.yaml",
    "deploy/prod/compose/35-sql.yaml",
}

SQL_MIGRATION_PROJECTS = {
    "services/tasks/assignment-api",
    "services/execution/api",
    "services/solutions/api",
}


@dataclass(frozen=True)
class Impact:
    contract: bool
    engines: bool
    contract_reasons: tuple[str, ...]
    engine_reasons: tuple[str, ...]


def normalize(path: str) -> str:
    return path.strip().replace("\\", "/").removeprefix("./")


def is_sql_migration(path: str) -> bool:
    p = PurePosixPath(path)
    text = p.as_posix()
    if "AddSqlDomain" not in p.name:
        return False
    return any(text.startswith(project + "/Migrations/") for project in SQL_MIGRATION_PROJECTS)


def classify(paths: list[str]) -> Impact:
    contract_reasons: list[str] = []
    engine_reasons: list[str] = []

    for raw in paths:
        path = normalize(raw)
        if not path:
            continue

        if path in CONTRACT_FILES or is_sql_migration(path) or any(path.startswith(prefix) for prefix in CONTRACT_PREFIXES):
            contract_reasons.append(path)

        if path in ENGINE_FILES or any(path.startswith(prefix) for prefix in ENGINE_PREFIXES):
            engine_reasons.append(path)

    return Impact(
        contract=bool(contract_reasons),
        engines=bool(engine_reasons),
        contract_reasons=tuple(dict.fromkeys(contract_reasons)),
        engine_reasons=tuple(dict.fromkeys(engine_reasons)),
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--field", choices=("contract", "engines"))
    parser.add_argument("--explain", action="store_true")
    args = parser.parse_args()

    paths = [line for line in sys.stdin.read().splitlines() if line.strip()]
    impact = classify(paths)

    if args.field:
        print("true" if getattr(impact, args.field) else "false")
        return 0

    payload = {
        "contract": impact.contract,
        "engines": impact.engines,
        "contractReasons": list(impact.contract_reasons),
        "engineReasons": list(impact.engine_reasons),
    }
    if args.explain:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    else:
        print(json.dumps(payload, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
