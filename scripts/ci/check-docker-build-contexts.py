#!/usr/bin/env python3
"""Static Docker build-context sanity checks for TaskForge CI.

The normal workflow builds many images with different contexts. A Dockerfile that
copies repository-level shared files must therefore be paired with a root build
context. This script catches the common CI-only failure before buildx starts.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = [
    ROOT / ".github" / "workflows" / "develop-build.yml",
    ROOT / ".github" / "workflows" / "develop-full-rebuild.yml",
]


def norm_context(value: str) -> Path:
    value = value.strip()
    if value.startswith("./"):
        value = value[2:]
    return ROOT / value


def normal_matrix_entries(text: str) -> list[tuple[str, Path, Path]]:
    entries: list[tuple[str, Path, Path]] = []
    pattern = re.compile(r'^\s*"([^|"\n]+)\|([^|"\n]+)\|([^|"\n]+)\|', re.MULTILINE)
    for name, context, dockerfile in pattern.findall(text):
        ctx = norm_context(context)
        entries.append((name, ctx, ctx / dockerfile))
    return entries


def full_matrix_entries(text: str) -> list[tuple[str, Path, Path]]:
    entries: list[tuple[str, Path, Path]] = []
    pattern = re.compile(
        r'^\s*- name: ([^\n]+)\n\s*context: ([^\n]+)\n\s*dockerfile: ([^\n]+)$',
        re.MULTILINE,
    )
    for name, context, dockerfile in pattern.findall(text):
        ctx = norm_context(context)
        entries.append((name.strip(), ctx, ctx / dockerfile.strip()))
    return entries


def copy_sources(dockerfile: Path) -> list[str]:
    sources: list[str] = []
    for raw in dockerfile.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = raw.strip()
        if not line.startswith("COPY ") or line.startswith("COPY --from"):
            continue
        if "[" in line:
            continue
        parts = line.split()
        if len(parts) < 3:
            continue
        for src in parts[1:-1]:
            if src in {".", "./"} or "*" in src or "${" in src:
                continue
            sources.append(src)
    return sources


def main() -> int:
    entries: list[tuple[str, Path, Path]] = []
    for workflow in WORKFLOWS:
        text = workflow.read_text(encoding="utf-8")
        if "full rebuild" in text or "full-rebuild" in workflow.name:
            entries.extend(full_matrix_entries(text))
        else:
            entries.extend(normal_matrix_entries(text))

    errors: list[str] = []
    seen: set[tuple[str, str]] = set()
    for name, ctx, dockerfile in entries:
        key = (name, dockerfile.as_posix())
        if key in seen:
            continue
        seen.add(key)
        if not dockerfile.exists():
            errors.append(f"{name}: Dockerfile is missing: {dockerfile.relative_to(ROOT)}")
            continue

        text = dockerfile.read_text(encoding="utf-8", errors="ignore")
        for src in copy_sources(dockerfile):
            path = (ctx / src).resolve()
            if not path.exists():
                errors.append(
                    f"{name}: Dockerfile COPY source is outside/missing for context "
                    f"{ctx.relative_to(ROOT)}: {src}"
                )

        service_sources = "".join(
            p.read_text(encoding="utf-8", errors="ignore")
            for p in dockerfile.parent.rglob("*.cs")
        )
        uses_cache = "AddTaskForgeRedisCache" in service_sources
        has_local_cache = "static IServiceCollection AddTaskForgeRedisCache" in service_sources
        if uses_cache and not has_local_cache:
            if "COPY Directory.Build.props global.json ./" not in text or "COPY services/shared services/shared" not in text:
                errors.append(
                    f"{name}: service uses AddTaskForgeRedisCache but Dockerfile does not copy "
                    "Directory.Build.props + services/shared before restore."
                )

    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1

    print(f"Docker build-context audit OK: {len(seen)} workflow image entries checked.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
