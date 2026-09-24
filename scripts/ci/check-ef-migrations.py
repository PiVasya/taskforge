#!/usr/bin/env python3
"""Fail CI when an EF Core model has changes that are not represented by migrations.

This checker never creates, edits, applies, or removes migrations. It uses the same
DbContext/project target list as scripts/generate-migrations.sh and runs EF Core's
`migrations has-pending-model-changes` command for each selected context.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GENERATOR = ROOT / "scripts/generate-migrations.sh"

_TARGET_RE = re.compile(r'^\s*"([^|\"]+)\|([^|\"]+)\|([^|\"]+)\|([^\"]+)"\s*$')
_NO_PENDING_RE = re.compile(r"No changes have been made|no pending model changes|нет изменений модели", re.IGNORECASE)
_PENDING_RE = re.compile(
    r"Changes have been made|pending model changes|has pending model changes|"
    r"Model changes were detected|model.*changed|"
    r"обнаружен[^ ]* изменен|обнаружены изменения|есть изменения модели|модель.*изменен",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class Target:
    alias: str
    project: str
    context: str
    output_dir: str


def fail(message: str, code: int = 2) -> "NoReturn":
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(code)


def load_targets() -> list[Target]:
    if not GENERATOR.is_file():
        fail(f"migration generator not found: {GENERATOR.relative_to(ROOT)}")

    targets: list[Target] = []
    in_items = False
    for raw in GENERATOR.read_text(encoding="utf-8").splitlines():
        line = raw.rstrip()
        if line.strip() == "ITEMS=(":
            in_items = True
            continue
        if in_items and line.strip() == ")":
            break
        if not in_items:
            continue
        match = _TARGET_RE.match(line)
        if match:
            targets.append(Target(*match.groups()))

    if not targets:
        fail("could not parse any DbContext targets from scripts/generate-migrations.sh")

    aliases = [target.alias for target in targets]
    contexts = [target.context for target in targets]
    if len(aliases) != len(set(aliases)):
        fail("duplicate migration target alias in scripts/generate-migrations.sh")
    if len(contexts) != len(set(contexts)):
        fail("duplicate DbContext in scripts/generate-migrations.sh")

    return targets


def select_targets(all_targets: list[Target], spec: str | None) -> list[Target]:
    if not spec or spec == "all":
        return all_targets

    by_name = {target.alias: target for target in all_targets}
    by_name.update({target.context: target for target in all_targets})
    selected: list[Target] = []
    seen: set[str] = set()
    for raw in spec.split(","):
        name = raw.strip()
        if not name:
            continue
        target = by_name.get(name)
        if target is None:
            valid = ", ".join(target.alias for target in all_targets)
            fail(f"unknown migration target '{name}'. Valid targets: {valid}")
        if target.context not in seen:
            selected.append(target)
            seen.add(target.context)

    if not selected:
        fail("no migration targets selected")
    return selected


def pending_from_output(output: str) -> bool:
    if _NO_PENDING_RE.search(output):
        return False
    return bool(_PENDING_RE.search(output))


def run(command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
    rendered = " ".join(command)
    print(f"+ {rendered}")
    return subprocess.run(
        command,
        cwd=ROOT,
        text=True,
        capture_output=capture,
        check=False,
    )


def require_success(command: list[str]) -> None:
    proc = run(command)
    if proc.returncode != 0:
        fail(f"command failed with exit code {proc.returncode}: {' '.join(command)}", proc.returncode)


def ensure_tooling() -> None:
    if shutil.which("dotnet") is None:
        fail(".NET SDK is required to check pending EF model changes")

    version = subprocess.run(
        ["dotnet", "--version"], cwd=ROOT, text=True, capture_output=True, check=False
    )
    if version.returncode != 0:
        fail("failed to query the installed .NET SDK", version.returncode)
    sdk = version.stdout.strip()
    if not sdk.startswith("10."):
        fail(f".NET 10 SDK is required, found: {sdk or '<unknown>'}")

    require_success(["dotnet", "tool", "restore"])


def check_target(target: Target, configuration: str) -> bool:
    project_path = ROOT / target.project
    if not project_path.is_file():
        fail(f"project file not found for target {target.alias}: {target.project}")

    print("========================================")
    print(f"TARGET: {target.alias}")
    print(f"CONTEXT: {target.context}")
    print(f"PROJECT: {target.project}")
    print(f"BUILD: {configuration}")
    print("========================================")

    require_success(["dotnet", "restore", target.project])
    require_success([
        "dotnet", "build", target.project,
        "-c", configuration,
        "--no-restore",
        "--nologo",
        "--verbosity", "minimal",
    ])

    # Probe the design-time context first. This keeps a startup/configuration/tooling
    # failure from being mistaken for the intentionally non-zero pending-model result.
    require_success([
        "dotnet", "ef", "dbcontext", "info",
        "--project", target.project,
        "--startup-project", target.project,
        "--context", target.context,
        "--configuration", configuration,
        "--no-build",
        "--no-color",
    ])

    command = [
        "dotnet", "ef", "migrations", "has-pending-model-changes",
        "--project", target.project,
        "--startup-project", target.project,
        "--context", target.context,
        "--configuration", configuration,
        "--no-build",
        "--no-color",
    ]
    proc = run(command, capture=True)
    output = (proc.stdout or "") + (proc.stderr or "")
    if output:
        print(output, end="" if output.endswith("\n") else "\n")

    if proc.returncode == 0:
        print(f"PASS: {target.context} matches its latest migration snapshot.")
        return True

    if pending_from_output(output):
        print(f"MIGRATION REQUIRED: {target.context}", file=sys.stderr)
        print(
            f"Run: ./scripts/generate-migrations.sh <MigrationName> {target.alias}",
            file=sys.stderr,
        )
        return False

    fail(
        f"EF returned exit code {proc.returncode} for {target.context}, but did not clearly report "
        "pending model changes. This is a tooling/design-time failure, not a migration verdict.",
        proc.returncode,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--targets",
        default="all",
        help="Comma-separated aliases/DbContext names from generate-migrations.sh; default: all",
    )
    parser.add_argument("--list", action="store_true", help="List DbContext targets and exit")
    args = parser.parse_args()

    all_targets = load_targets()
    if args.list:
        for target in all_targets:
            print(f"{target.alias}\t{target.context}\t{target.project}")
        return 0

    selected = select_targets(all_targets, args.targets)
    ensure_tooling()
    configuration = os.environ.get("DOTNET_BUILD_CONFIGURATION", "Release")

    pending: list[Target] = []
    for target in selected:
        if not check_target(target, configuration):
            pending.append(target)

    if pending:
        print("\nFAIL: EF model changes without matching migrations:", file=sys.stderr)
        for target in pending:
            print(f"  - {target.alias}: {target.context}", file=sys.stderr)
        print(
            "Generate the intended migration(s), review them, commit the migration + Designer + updated ModelSnapshot, then rerun CI.",
            file=sys.stderr,
        )
        return 1

    print(f"\nPASS: {len(selected)} EF DbContext(s) have no pending model changes.")
    print("No migration was generated or applied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
