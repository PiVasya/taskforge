#!/usr/bin/env python3
"""Fail fast when Dockerfiles, prod compose images and develop-build matrix drift apart."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "develop-build.yml"
PROD_COMPOSE = ROOT / "deploy" / "prod" / "compose"


def norm(path: Path) -> str:
    return path.as_posix().lstrip("./")


def matrix_entries() -> dict[str, str]:
    text = WORKFLOW.read_text(encoding="utf-8")
    entries: dict[str, str] = {}
    pattern = re.compile(r'^\s*"([^|"\n]+)\|([^|"\n]+)\|([^|"\n]+)\|', re.MULTILINE)
    for name, context, dockerfile in pattern.findall(text):
        context_path = Path(context.replace("./", "", 1))
        full = (ROOT / context_path / dockerfile).resolve()
        entries[name] = norm(full.relative_to(ROOT))
    return entries


def project_dockerfiles() -> set[str]:
    ignored_prefixes = ("docs/original/",)
    files = set()
    for path in ROOT.rglob("Dockerfile"):
        rel = norm(path.relative_to(ROOT))
        if rel.startswith(ignored_prefixes):
            continue
        files.add(rel)
    return files


def prod_images() -> set[str]:
    names: set[str] = set()
    for path in sorted(PROD_COMPOSE.glob("*.y*ml")):
        text = path.read_text(encoding="utf-8")
        for image in re.findall(r'image:\s*\$\{IMAGE_REPOSITORY[^}]*\}/([^:\s]+):\$\{IMAGE_TAG', text):
            names.add(image)
    return names


def main() -> int:
    matrix = matrix_entries()
    dockerfiles = project_dockerfiles()
    prod = prod_images()

    errors: list[str] = []
    matrix_dockerfiles = set(matrix.values())

    missing_dockerfiles = sorted(path for path in matrix_dockerfiles if not (ROOT / path).exists())
    if missing_dockerfiles:
        errors.append("Matrix points to missing Dockerfiles:\n  " + "\n  ".join(missing_dockerfiles))

    uncovered = sorted(dockerfiles - matrix_dockerfiles)
    if uncovered:
        errors.append("Dockerfiles not covered by develop build matrix:\n  " + "\n  ".join(uncovered))

    matrix_extra = sorted(matrix_dockerfiles - dockerfiles)
    if matrix_extra:
        errors.append("Matrix Dockerfiles not present in project scan:\n  " + "\n  ".join(matrix_extra))

    prod_missing = sorted(prod - set(matrix))
    if prod_missing:
        errors.append("Prod compose images missing from develop build matrix:\n  " + "\n  ".join(prod_missing))

    matrix_not_prod = sorted(set(matrix) - prod)
    if matrix_not_prod:
        errors.append("Matrix images not used by prod compose:\n  " + "\n  ".join(matrix_not_prod))

    if errors:
        print("\n\n".join(errors), file=sys.stderr)
        return 1

    print(f"Workflow integrity OK: {len(matrix)} matrix images, {len(dockerfiles)} Dockerfiles, {len(prod)} prod images.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
