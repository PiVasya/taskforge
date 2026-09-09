#!/usr/bin/env python3
"""Check the SQL model-check's source and restored EF dependency graph, offline."""
from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APIS = (
    'services/tasks/assignment-api/TaskForge.Tasks.Api.csproj',
    'services/execution/api/TaskForge.Execution.Api.csproj',
    'services/solutions/api/TaskForge.Solutions.Api.csproj',
)
CHECK_PROJECT = 'tools/sql-domain-check/TaskForge.Sql.DomainCheck.csproj'
RUNTIME_PACKAGES = (
    'Microsoft.EntityFrameworkCore',
    'Microsoft.EntityFrameworkCore.Relational',
)
RESOLVED_PACKAGES = (*RUNTIME_PACKAGES, 'Microsoft.EntityFrameworkCore.Abstractions')
DESIGN_PACKAGE = 'Microsoft.EntityFrameworkCore.Design'
PROVIDER_PACKAGE = 'Npgsql.EntityFrameworkCore.PostgreSQL'


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def metadata(item: ET.Element, name: str) -> str:
    return (item.get(name) or item.findtext(name) or '').strip()


def tokens(value: str) -> set[str]:
    return {part.strip().lower() for part in value.split(';') if part.strip()}


def check_sources(root: Path) -> str:
    manifest = json.loads((root / '.config/dotnet-tools.json').read_text(encoding='utf-8'))
    version = manifest['tools']['dotnet-ef']['version']
    require(isinstance(version, str) and len(version.split('.')) == 3
            and all(part.isdigit() for part in version.split('.')), 'EF CLI needs an exact release version.')
    provider_versions: set[str] = set()
    for name in APIS:
        project = ET.parse(root / name).getroot()
        refs: dict[str, ET.Element] = {}
        for item in project.findall('.//PackageReference'):
            package = item.get('Include', '')
            require(package not in refs, f'{name}: duplicate PackageReference {package}.')
            refs[package] = item
        for package in (*RUNTIME_PACKAGES, DESIGN_PACKAGE):
            require(package in refs, f'{name}: missing explicit {package}; private Design dependencies cannot pin consumer runtime.')
            item = refs[package]
            require(metadata(item, 'Version') == version, f'{name}: {package} must be {version}.')
            require(not item.get('Condition'), f'{name}: the {package} pin must be unconditional.')
        require(tokens(metadata(refs[DESIGN_PACKAGE], 'PrivateAssets')) == {'all'},
                f'{name}: Design must stay private; do not expose tooling to consumers.')
        for package in RUNTIME_PACKAGES:
            item = refs[package]
            for field in ('PrivateAssets', 'ExcludeAssets'):
                require(not tokens(metadata(item, field)) & {'all', 'compile', 'runtime'},
                        f'{name}: {package} {field} blocks transitive compile/runtime assets.')
            included = tokens(metadata(item, 'IncludeAssets'))
            require(not included or 'all' in included or {'compile', 'runtime'} <= included,
                    f'{name}: {package} must include compile and runtime assets.')
        require(PROVIDER_PACKAGE in refs, f'{name}: Npgsql provider is missing.')
        provider_version = metadata(refs[PROVIDER_PACKAGE], 'Version')
        require(bool(provider_version), f'{name}: Npgsql provider version is missing.')
        provider_versions.add(provider_version)
    require(len(provider_versions) == 1, 'The three API projects use different Npgsql provider versions.')

    path = root / CHECK_PROJECT
    project = ET.parse(path).getroot()
    actual = set()
    for item in project.findall('.//ProjectReference'):
        include = item.get('Include', '').replace('\\', '/')
        require(bool(include), 'The model-check contains an empty ProjectReference.')
        actual.add((path.parent / include).resolve())
    require(actual == {(root / name).resolve() for name in APIS},
            'The model-check must reference exactly the three migration API projects.')
    return version


def check_assets(root: Path, version: str) -> None:
    """Inspect NuGet's real restore output, not a reconstructed dependency solver."""
    for name in (*APIS, CHECK_PROJECT):
        path = (root / name).parent / 'obj/project.assets.json'
        require(path.is_file(), f'{name}: restore output missing; run dotnet restore first.')
        assets = json.loads(path.read_text(encoding='utf-8'))
        targets = assets.get('targets', {})
        require(bool(targets), f'{name}: restore output has no targets.')
        failures = [entry.get('code', 'unknown') for entry in assets.get('logs', [])
                    if str(entry.get('level', '')).lower() == 'error']
        require(not failures, f'{name}: restore output contains errors: {", ".join(failures)}.')
        for target, libraries in targets.items():
            require(target == 'net10.0' or target.startswith('net10.0/'),
                    f'{name}: unexpected restored target {target}.')
            for package in RESOLVED_PACKAGES:
                actual = {key.rsplit('/', 1)[1] for key in libraries
                          if key.rsplit('/', 1)[0] == package}
                require(actual == {version},
                        f'{name} [{target}]: restored {package} is {sorted(actual)}, expected [{version}].')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--resolved', action='store_true', help='Also validate obj/project.assets.json after dotnet restore.')
    args = parser.parse_args()
    try:
        version = check_sources(ROOT)
        print(f'PASS: three APIs expose EF Core/Relational {version}; Design stays private; CLI matches.')
        if args.resolved:
            check_assets(ROOT, version)
            print(f'PASS: all four restored graphs select EF Core, Relational and Abstractions {version}.')
        else:
            print('SOURCE CHECK ONLY: restore output and C# compilation have not been checked.')
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        print(f'FAIL: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
