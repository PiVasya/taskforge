#!/usr/bin/env python3
"""Stable, source-only guard for the SQL execution boundary.

This script deliberately checks only invariants that are dangerous to regress and
can be verified reliably without building the project:

* the Go worker must not grow a Python runtime dependency;
* C# and Go must agree on the versioned SQL wire/adapter/runtime identities;
* disposable SQL engine containers must stay on private internal networks with no
  published host ports and with basic container hardening;
* SQL-owned C# code must not fall back to the generic Assignment.TestsJson payload.

It intentionally does NOT freeze filenames, source hashes, implementation details,
workflow text, seccomp source lines, UI modules, or the complete repository tree.
Those concerns belong to build/tests, workflow-integrity, EF checks and the Go
runtime/security tests.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import re
import sys
from typing import Any, Iterable

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKER_ROOT = ROOT / "services/execution/sql-worker"
COMPOSE_FILES = (
    ROOT / "deploy/dev/compose/35-sql.yaml",
    ROOT / "deploy/prod/compose/35-sql.yaml",
)


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


def load_yaml(path: Path, guard: Guard) -> dict[str, Any] | None:
    text = read_text(path, guard)
    if text is None:
        return None
    try:
        data = yaml.safe_load(text)
    except yaml.YAMLError as error:
        guard.errors.append(f"invalid YAML in {path.relative_to(ROOT)}: {error}")
        return None
    if not isinstance(data, dict):
        guard.errors.append(f"{path.relative_to(ROOT)} must contain a YAML mapping")
        return None
    return data


def env_map(service: dict[str, Any]) -> dict[str, str]:
    raw = service.get("environment") or {}
    if isinstance(raw, dict):
        return {str(key): "" if value is None else str(value) for key, value in raw.items()}
    result: dict[str, str] = {}
    if isinstance(raw, list):
        for item in raw:
            text = str(item)
            key, separator, value = text.partition("=")
            result[key] = value if separator else ""
    return result


def network_names(service: dict[str, Any]) -> set[str]:
    raw = service.get("networks") or []
    if isinstance(raw, dict):
        return {str(name) for name in raw}
    if isinstance(raw, list):
        return {str(name) for name in raw}
    return set()


def list_values(value: Any) -> list[str]:
    if value is None:
        return []
    if isinstance(value, list):
        return [str(item) for item in value]
    return [str(value)]


def labels(service: dict[str, Any]) -> dict[str, str]:
    raw = service.get("labels") or {}
    if isinstance(raw, dict):
        return {str(key): str(value) for key, value in raw.items()}
    result: dict[str, str] = {}
    if isinstance(raw, list):
        for item in raw:
            key, separator, value = str(item).partition("=")
            if separator:
                result[key] = value
    return result


def health_text(service: dict[str, Any]) -> str:
    health = service.get("healthcheck") or {}
    if not isinstance(health, dict):
        return ""
    test = health.get("test") or []
    if isinstance(test, list):
        return " ".join(str(part) for part in test)
    return str(test)


def discover_worker(services: dict[str, Any]) -> tuple[str, dict[str, Any]] | None:
    """Find the SQL worker by behavior/build metadata, not by a fixed service name."""
    matches: list[tuple[str, dict[str, Any]]] = []
    for name, raw in services.items():
        if not isinstance(raw, dict):
            continue
        build = raw.get("build") or {}
        if isinstance(build, str):
            build_context = build
        elif isinstance(build, dict):
            build_context = str(build.get("context", ""))
        else:
            build_context = ""
        image = str(raw.get("image", ""))
        health = health_text(raw)
        score = sum((
            "sql-worker" in build_context,
            "sql-worker" in image,
            "sql-worker" in health and "health" in health,
        ))
        if score >= 2:
            matches.append((str(name), raw))
    return matches[0] if len(matches) == 1 else None


def is_internal_network(config: Any) -> bool:
    return isinstance(config, dict) and config.get("internal") is True


def hardened(service: dict[str, Any]) -> bool:
    opts = " ".join(list_values(service.get("security_opt"))).lower()
    dropped = {value.upper() for value in list_values(service.get("cap_drop"))}
    return service.get("read_only") is True and "no-new-privileges" in opts and "ALL" in dropped


def check_compose(path: Path, guard: Guard) -> None:
    compose = load_yaml(path, guard)
    if compose is None:
        return
    services = compose.get("services")
    networks = compose.get("networks") or {}
    rel = path.relative_to(ROOT)
    if not isinstance(services, dict) or not isinstance(networks, dict):
        guard.errors.append(f"{rel}: services/networks section is malformed")
        return

    worker_match = discover_worker(services)
    if worker_match is None:
        guard.errors.append(f"{rel}: could not identify exactly one SQL worker service")
        return
    worker_name, worker = worker_match

    worker_networks = network_names(worker)
    private_networks = {name for name in worker_networks if is_internal_network(networks.get(name))}
    guard.require(
        len(private_networks) >= 2,
        f"{rel}: SQL worker must remain attached to separate internal database networks",
    )
    guard.require(hardened(worker), f"{rel}: SQL worker lost read-only/no-new-privileges/cap-drop hardening")
    guard.require(not worker.get("ports"), f"{rel}: SQL worker must not publish host ports")
    worker_health = health_text(worker).lower()
    guard.require(
        "sql-worker" in worker_health and "health" in worker_health,
        f"{rel}: SQL worker healthcheck must execute the worker health command",
    )

    engine_names: set[str] = set()
    for network_name in private_networks:
        members = {
            str(name)
            for name, raw in services.items()
            if name != worker_name and isinstance(raw, dict) and network_name in network_names(raw)
        }
        guard.require(
            len(members) == 1,
            f"{rel}: internal SQL network {network_name!r} must contain exactly one engine plus the worker",
        )
        engine_names.update(members)

    # The current product contract supports external PostgreSQL and MySQL sandboxes;
    # SQLite is embedded in the worker and therefore has no Compose service/network.
    guard.require(
        len(engine_names) >= 2,
        f"{rel}: expected isolated external SQL engine services for PostgreSQL and MySQL",
    )

    for engine_name in sorted(engine_names):
        engine = services.get(engine_name)
        if not isinstance(engine, dict):
            continue
        engine_networks = network_names(engine)
        external_networks = {name for name in engine_networks if not is_internal_network(networks.get(name))}
        guard.require(
            not external_networks,
            f"{rel}: sandbox engine {engine_name!r} escaped onto non-internal network(s): {sorted(external_networks)}",
        )
        guard.require(not engine.get("ports"), f"{rel}: sandbox engine {engine_name!r} publishes host ports")
        guard.require(hardened(engine), f"{rel}: sandbox engine {engine_name!r} lost container hardening")

    # Keep disposable SQL infrastructure out of the HA-critical set without caring
    # about the exact service names.
    for service_name in {worker_name, *engine_names}:
        service = services.get(service_name)
        if not isinstance(service, dict):
            continue
        critical = labels(service).get("taskforge.ha.critical")
        guard.require(
            str(critical).lower() == "false",
            f"{rel}: disposable SQL service {service_name!r} must stay non-HA-critical",
        )


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
    for path in COMPOSE_FILES:
        check_compose(path, guard)
    check_sql_payload_boundary(guard)
    guard.finish()

    print("PASS: SQL runtime boundary guard")
    print("  - Go worker stays free of Python runtime dependencies")
    print("  - C#/Go wire, adapter and execution-semantics versions agree")
    print("  - PostgreSQL/MySQL sandboxes stay private, unexposed and hardened in dev/prod Compose")
    print("  - SQL-owned C# code stays off generic TestsJson")
    print("DELEGATED: build/race/seccomp/runtime behavior -> scripts/check-sql-go.sh")
    print("DELEGATED: EF/domain correctness -> scripts/check-sql-domain.sh + check-ef-migrations.py")
    print("DELEGATED: CI wiring/change-impact -> scripts/ci/check-workflow-integrity.py + test-sql-change-impact.py")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
