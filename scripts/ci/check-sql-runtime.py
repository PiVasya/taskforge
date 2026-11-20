#!/usr/bin/env python3
"""Stable, source-only guard for the SQL execution boundary.

This script deliberately checks only invariants that are dangerous to regress and
can be verified reliably without building the project:

* the Go worker must not grow a Python runtime dependency;
* C# and Go must agree on the versioned SQL wire/adapter/runtime identities;
* SQL-owned C# code must not fall back to the generic Assignment.TestsJson payload.

It intentionally does NOT freeze filenames, source hashes, implementation details,
workflow text, seccomp source lines, UI modules, or the complete repository tree.
K3s network isolation is checked separately by `check-k3s-release-boundaries.py`;
this guard does not pretend to validate deleted dev/prod Compose files.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import re
import sys
from typing import Iterable


ROOT = Path(__file__).resolve().parents[2]
WORKER_ROOT = ROOT / "services/execution/sql-worker"
@dataclass
class Guard:
    errors: list[str] = field(default_factory=list)

    def require(self, condition: bool, message: str) -> None:
        if not condition:
            self.errors.append(message)

    def finish(self) -> None:
        if not self.errors:
            return
        print(f"FAIL: SQL runtime guard found {len(self.errors)} problem(s):", file=sys.stderr)
        for index, message in enumerate(self.errors, 1):
            print(f"  {index}. {message}", file=sys.stderr)
        raise SystemExit(1)


def read_text(path: Path, guard: Guard) -> str | None:
    if not path.is_file():
        guard.errors.append(f"required SQL runtime file is missing: {path.relative_to(ROOT)}")
        return None
    try:
        return path.read_text(encoding="utf-8-sig")
    except OSError as error:
        guard.errors.append(f"cannot read {path.relative_to(ROOT)}: {error}")
        return None


def find_named_constant(
    roots: Iterable[Path],
    suffix: str,
    pattern: re.Pattern[str],
    label: str,
    guard: Guard,
) -> str | None:
    matches: list[tuple[Path, str]] = []
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob(f"*{suffix}"):
            try:
                text = path.read_text(encoding="utf-8-sig")
            except OSError:
                continue
            for match in pattern.finditer(text):
                matches.append((path, match.group(1)))
    if len(matches) != 1:
        locations = ", ".join(str(path.relative_to(ROOT)) for path, _ in matches) or "none"
        guard.errors.append(f"expected exactly one {label}; found {len(matches)} ({locations})")
        return None
    return matches[0][1]


def check_cross_language_contract(guard: Guard) -> None:
    go_roots = (WORKER_ROOT,)
    csharp_roots = (ROOT / "services/shared/Sql", ROOT / "services/tasks/assignment-api/Domain/Sql")

    go_contract = find_named_constant(
        go_roots,
        ".go",
        re.compile(r'(?m)^\s*const\s+ContractVersion\s*=\s*(\d+)\s*$'),
        "Go SQL ContractVersion",
        guard,
    )
    cs_contract = find_named_constant(
        csharp_roots,
        ".cs",
        re.compile(r'(?m)^\s*public\s+const\s+int\s+Version\s*=\s*(\d+)\s*;'),
        "C# SqlWire.Version",
        guard,
    )
    if go_contract is not None and cs_contract is not None:
        guard.require(go_contract == cs_contract, f"SQL wire version drift: Go={go_contract}, C#={cs_contract}")

    go_adapter = find_named_constant(
        go_roots,
        ".go",
        re.compile(r'(?m)^\s*const\s+AdapterVersion\s*=\s*"([^"]+)"\s*$'),
        "Go SQL AdapterVersion",
        guard,
    )
    cs_adapter = find_named_constant(
        csharp_roots,
        ".cs",
        re.compile(r'(?m)^\s*public\s+const\s+string\s+AdapterVersion\s*=\s*"([^"]+)"\s*;'),
        "C# SqlWire.AdapterVersion",
        guard,
    )
    if go_adapter is not None and cs_adapter is not None:
        guard.require(go_adapter == cs_adapter, f"SQL adapter version drift: Go={go_adapter}, C#={cs_adapter}")

    go_semantics = find_named_constant(
        go_roots,
        ".go",
        re.compile(r'(?m)^\s*const\s+ExecutionSemanticsVersion\s*=\s*"([^"]+)"\s*$'),
        "Go SQL ExecutionSemanticsVersion",
        guard,
    )
    cs_semantics = find_named_constant(
        csharp_roots,
        ".cs",
        re.compile(r'(?m)^\s*public\s+const\s+string\s+CurrentExecutionSemanticsVersion\s*=\s*"([^"]+)"\s*;'),
        "C# current SQL execution semantics version",
        guard,
    )
    if go_semantics is not None and cs_semantics is not None:
        guard.require(
            go_semantics == cs_semantics,
            f"SQL execution semantics drift: Go={go_semantics}, C#={cs_semantics}",
        )


def check_worker_language_boundary(guard: Guard) -> None:
    if not WORKER_ROOT.is_dir():
        guard.errors.append("SQL worker directory is missing: services/execution/sql-worker")
        return

    python_sources = sorted(path.relative_to(ROOT) for path in WORKER_ROOT.rglob("*.py"))
    python_manifests = sorted(
        path.relative_to(ROOT)
        for name in ("requirements.txt", "pyproject.toml", "Pipfile", "poetry.lock")
        if (path := WORKER_ROOT / name).exists()
    )
    guard.require(
        not python_sources and not python_manifests,
        "Python runtime/dependency files were reintroduced into the Go SQL worker: "
        + ", ".join(str(path) for path in [*python_sources, *python_manifests]),
    )

    dockerfile = read_text(WORKER_ROOT / "Dockerfile", guard)
    if dockerfile is not None:
        lowered = dockerfile.lower()
        guard.require("go build" in lowered, "SQL worker Dockerfile no longer builds the Go worker")
        guard.require(
            not re.search(r"(?m)^\s*(run\s+.*\b(?:pip|python)\b|from\s+python(?::|@|\s))", lowered),
            "SQL worker production Dockerfile reintroduced Python/pip",
        )


def is_sql_owned_csharp(path: Path) -> bool:
    rel = path.relative_to(ROOT)
    parts_lower = {part.lower() for part in rel.parts}
    return "sql" in parts_lower or path.stem.lower().startswith("sql")


def check_sql_payload_boundary(guard: Guard) -> None:
    roots = (
        ROOT / "services/tasks/assignment-api",
        ROOT / "services/solutions/api",
        ROOT / "services/execution/api",
    )
    offenders: list[Path] = []
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*.cs"):
            if not is_sql_owned_csharp(path):
                continue
            try:
                text = path.read_text(encoding="utf-8-sig")
            except OSError:
                continue
            if re.search(r"\bTestsJson\b", text):
                offenders.append(path.relative_to(ROOT))
    guard.require(
        not offenders,
        "SQL-owned C# code must not use the generic TestsJson payload: "
        + ", ".join(str(path) for path in offenders),
    )


def main() -> int:
    guard = Guard()
    check_worker_language_boundary(guard)
    check_cross_language_contract(guard)
    check_sql_payload_boundary(guard)
    guard.finish()

    print("PASS: SQL runtime boundary guard")
    print("  - Go worker stays free of Python runtime dependencies")
    print("  - C#/Go wire, adapter and execution-semantics versions agree")
    print("  - K3s network and OCI container boundaries are checked by check-k3s-release-boundaries.py")
    print("  - SQL-owned C# code stays off generic TestsJson")
    print("DELEGATED: build/race/seccomp/runtime behavior -> scripts/check-sql-go.sh")
    print("DELEGATED: EF/domain correctness -> scripts/check-sql-domain.sh + check-ef-migrations.py")
    print("DELEGATED: K3s container/CI wiring -> check-k3s-release-boundaries.py + check-k3s-migration-contract.py")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
