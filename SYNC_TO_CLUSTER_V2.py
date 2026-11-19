#!/usr/bin/env python3
"""Safely replace a CLEAN cluster-v2 Git checkout with this full K3s source tree.

Usage (from unpacked ZIP, not from the Git checkout):
    python3 SYNC_TO_CLUSTER_V2.py /absolute/path/to/clean/cluster-v2/checkout

The script never commits, pushes, touches any other branch, removes .git, or
removes untracked files that are unrelated to this source package.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

REQUIRED = (
    ".github/workflows/k3s-sandbox-ci.yml",
    "tools/k3s/local/generate.mjs",
    "infrastructure/k8s/taskforge-yugabyte/local/run-local.sh",
    "README_K3S_BRANCH.md",
    "infrastructure/k8s/taskforge-yugabyte/db/create-databases.sql",
    "infrastructure/k8s/taskforge-yugabyte/db/create-k3s-lab-databases.sql",
)
GENERATED_DIRS = {"__pycache__", "node_modules", ".pytest_cache", ".git", "bin", "obj"}

LEGACY_PATHS = (
    "apps/web-ct",
    "services/ai/worker",
    "services/tasks/quiz-api",
    "deploy/prod",
    "deploy/dev",
    "deploy/cluster",
    ".github/workflows/develop-build.yml",
    ".github/workflows/develop-full-rebuild.yml",
    ".github/workflows/minecraft-plugins-build.yml",
)


def git(target: Path, *args: str) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(target), *args], check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    return result.stdout


def inventory(source: Path) -> dict[str, Path]:
    if (source / ".git").exists():
        raise ValueError("Source must be an EXTRACTED ZIP, not another Git checkout")
    for marker in REQUIRED:
        if not (source / marker).is_file():
            raise ValueError(f"ZIP source is missing required K3s file: {marker}")
    for old in LEGACY_PATHS:
        if (source / old).exists():
            raise ValueError(f"Refusing to import legacy production path: {old}")
    found: dict[str, Path] = {}
    for item in source.rglob("*"):
        relative = item.relative_to(source)
        if any(part in GENERATED_DIRS for part in relative.parts) or item.suffix == ".pyc":
            continue
        if item.is_symlink():
            raise ValueError(f"ZIP source contains unsupported symlink: {item}")
        if item.is_file():
            key = item.relative_to(source).as_posix()
            found[key] = item
    if len(found) < len(REQUIRED) or "SYNC_TO_CLUSTER_V2.py" not in found:
        raise ValueError("ZIP source inventory looks incomplete")
    return found


def sync(source: Path, target: Path, dry_run: bool = False) -> tuple[int, int]:
    source = source.resolve()
    target = target.resolve()
    if source == target or source in target.parents or target in source.parents:
        raise ValueError("Source ZIP folder and target Git checkout must be SEPARATE directories")
    if not target.is_dir() or not (target / ".git").exists():
        raise ValueError("Target is not a checked-out Git repository")
    toplevel = Path(os.fsdecode(git(target, "rev-parse", "--show-toplevel")).strip()).resolve()
    if toplevel != target:
        raise ValueError(f"Target must be repository root, not {target} inside {toplevel}")
    branch = os.fsdecode(git(target, "branch", "--show-current")).strip()
    if branch != "cluster-v2":
        raise ValueError(f"REFUSING: current branch is {branch!r}, expected 'cluster-v2'")
    sparse = subprocess.run(
        ["git", "-C", str(target), "config", "--bool", "core.sparseCheckout"],
        check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    ).stdout.strip()
    if sparse == b"true":
        raise ValueError("Sparse checkouts are not supported; use a full fresh clone")
    if git(target, "status", "--porcelain", "--untracked-files=all").strip():
        raise ValueError("Target checkout is dirty; use a FRESH clone with no local changes")
    src = inventory(source)
    tracked = {
        os.fsdecode(entry)
        for entry in git(target, "ls-files", "-z").split(b"\0")
        if entry
    }
    obsolete = sorted(tracked - src.keys())
    incoming = sorted(src)
    print(f"Verified branch {branch}: {len(src)} ZIP files, {len(tracked)} tracked Git files")
    print(f"Obsolete tracked paths that will be removed: {len(obsolete)}")
    for old in LEGACY_PATHS:
        matches = sum(p == old or p.startswith(old + "/") for p in obsolete)
        if matches:
            print(f"  DELETE {old} ({matches} tracked file(s))")
    if dry_run:
        print("DRY RUN: no files were changed. Remove --dry-run to apply.")
        return len(incoming), len(obsolete)

    # Preflight ALL targets before doing any writes, including symlink escapes.
    for relative in incoming:
        dest = target / relative
        for parent in [dest, *dest.parents]:
            if parent == target.parent:
                break
            if parent.is_symlink():
                raise ValueError(f"Refusing to overwrite path through a symlink: {relative}")
        if dest.exists() and dest.is_dir():
            raise ValueError(f"Target contains a directory at source-file path {relative}")
    for relative in obsolete:
        dest = target / relative
        if dest.is_symlink():
            # An old tracked symlink can be removed without following it.
            continue
        if dest.is_dir():
            raise ValueError(f"Tracked path unexpectedly became directory: {relative}")

    for relative in obsolete:
        dest = target / relative
        if dest.is_file() or dest.is_symlink():
            dest.unlink()
    # Git does not track directories. Remove empty legacy directories after unlink.
    for relative in obsolete:
        parent = (target / relative).parent
        while parent != target and parent.is_dir():
            try:
                parent.rmdir()
            except OSError:
                break
            parent = parent.parent
    for relative in incoming:
        dest = target / relative
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src[relative], dest)

    # Stage only existing tracked changes and explicitly supplied ZIP files.
    # Never git add -A over unrelated untracked local files or secrets.
    subprocess.run(["git", "-C", str(target), "add", "-u"], check=True)
    for index in range(0, len(incoming), 70):
        batch = incoming[index:index + 70]
        subprocess.run(["git", "-C", str(target), "add", "-f", "--", *batch], check=True)
    for old in LEGACY_PATHS:
        if (target / old).exists():
            raise ValueError(f"Legacy path remains after synchronization: {old}")
    print("PASS: exactly this ZIP's files staged in cluster-v2, including real deletions")
    subprocess.run(["git", "-C", str(target), "diff", "--cached", "--stat"], check=True)
    print("Next: git diff --cached --check; git commit; git push origin cluster-v2")
    return len(incoming), len(obsolete)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", type=Path, help="PATH TO A FRESH cluster-v2 GIT CHECKOUT")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        sync(Path(__file__).resolve().parent, args.target, dry_run=args.dry_run)
    except (ValueError, OSError, subprocess.CalledProcessError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
