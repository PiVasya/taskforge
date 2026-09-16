#!/usr/bin/env python3
"""Build a matrix containing only .NET production projects affected by changed paths."""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def norm(raw: str) -> str:
    return raw.strip().replace('\\', '/').lstrip('./')


def production_projects() -> list[Path]:
    return sorted(
        p for p in (ROOT / 'services').rglob('*.csproj')
        if '/tests/' not in '/' + p.relative_to(ROOT).as_posix() + '/'
    )


def watched_prefixes(project: Path) -> set[str]:
    prefixes = {project.parent.relative_to(ROOT).as_posix() + '/'}
    text = project.read_text(encoding='utf-8', errors='ignore')
    for include in re.findall(r'<Compile\s+Include="([^"]+)"', text):
        if '*' not in include:
            candidate = (project.parent / include).resolve()
            try:
                prefixes.add(candidate.relative_to(ROOT).as_posix())
            except ValueError:
                pass
            continue
        static = include.split('*', 1)[0].rstrip('/\\')
        candidate = (project.parent / static).resolve()
        try:
            rel = candidate.relative_to(ROOT).as_posix().rstrip('/') + '/'
            prefixes.add(rel)
        except ValueError:
            pass
    return prefixes


def affected(paths: list[str]) -> list[Path]:
    projects = production_projects()
    if any(path in {'Directory.Build.props', 'global.json', 'scripts/tests/dotnet.sh', 'scripts/tests/dotnet-project.sh', 'scripts/ci/dotnet-change-matrix.py'} for path in paths):
        return projects

    result: list[Path] = []
    for project in projects:
        prefixes = watched_prefixes(project)
        if any(any(path == p.rstrip('/') or path.startswith(p) for p in prefixes) for path in paths):
            result.append(project)
    return result


def matrix(paths: list[str]) -> dict[str, list[dict[str, str]]]:
    return {
        'include': [
            {
                'name': '-'.join(project.parent.relative_to(ROOT / 'services').parts),
                'project': project.relative_to(ROOT).as_posix(),
            }
            for project in affected(paths)
        ]
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--count', action='store_true')
    parser.add_argument('--explain', action='store_true')
    args = parser.parse_args()
    paths = [norm(line) for line in sys.stdin.read().splitlines() if norm(line)]
    value = matrix(paths)
    if args.count:
        print(len(value['include']))
    elif args.explain:
        if value['include']:
            for item in value['include']:
                print(f"{item['name']}: {item['project']}")
        else:
            print('<none>')
    else:
        print(json.dumps(value, separators=(',', ':')))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
