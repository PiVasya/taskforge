#!/usr/bin/env python3
"""Fail fast if a K3s Docker image omits .csproj-linked C# files.

The root .NET project builds can pass while a Docker build fails because COPY
omits shared sources. This checks all six core API compile inputs inside the
pre-publish stage and the frontend's npm inputs without running Docker.
"""
from __future__ import annotations

from pathlib import Path
import re
import shlex
import sys
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SPECS = [
    ("identity", "services/identity/api", "services/identity/api/TaskForge.Identity.Api.csproj", "services/identity/api/Dockerfile", "."),
    ("education", "services/education/api", "services/education/api/TaskForge.Education.Api.csproj", "services/education/api/Dockerfile", "."),
    ("content", "services/content/api", "services/content/api/learning-content-service.csproj", "services/content/api/Dockerfile", "services/content/api"),
    ("tasks", "services/tasks/assignment-api", "services/tasks/assignment-api/TaskForge.Tasks.Api.csproj", "services/tasks/assignment-api/Dockerfile", "."),
    ("solutions", "services/solutions/api", "services/solutions/api/TaskForge.Solutions.Api.csproj", "services/solutions/api/Dockerfile", "."),
    ("execution", "services/execution/api", "services/execution/api/TaskForge.Execution.Api.csproj", "services/execution/api/Dockerfile", "."),
]


def image_copied_paths(dockerfile: Path, context: Path) -> set[Path]:
    copies = set()
    contents = dockerfile.read_text(encoding="utf-8")
    # All checked Dockerfiles use simple COPY lines. Require adding parser support
    # before introducing unsupported advanced COPY syntax rather than ignoring it.
    for line in contents.splitlines():
        line = line.strip()
        if line.startswith('RUN dotnet publish '):
            break
        if not line.startswith('COPY '):
            continue
        parts = shlex.split(line)
        if any(a.startswith('--') for a in parts[1:]):
            raise AssertionError(f"Unsupported prepublish COPY in {dockerfile}: {line}")
        if len(parts) != 3:
            raise AssertionError(f"Invalid COPY in {dockerfile}: {line}")
        src, dest = parts[1:]
        source = (context / src).resolve()
        if not source.exists():
            raise AssertionError(f"Docker COPY source not found: {dockerfile}: {src}")
        destination = Path(dest)
        if destination.is_absolute():
            # All core .NET project COPY targets are relative to /src (content
            # service project relative to its own workdir).
            destination = Path(*destination.parts[1:])
        if source.is_dir():
            for item in source.rglob('*'):
                if item.is_file():
                    copies.add(destination / item.relative_to(source))
        else:
            copies.add(destination / source.name if dest.endswith('/') else destination)
    return copies


def check():
    for name, projectdir, proj, dockerfile, context in SPECS:
        proj_path = ROOT / proj
        csproj = ET.parse(proj_path).getroot()
        copy_paths = image_copied_paths(ROOT / dockerfile, ROOT / context)
        project_context_dir = Path(projectdir) if context == '.' else Path('.')
        if not (project_context_dir / proj_path.name) in copy_paths:
            raise AssertionError(f"{name}: project file not copied by Dockerfile")
        sources = list((ROOT / projectdir).rglob('*.cs'))
        # Ensure at least application entrypoint is present in the SDK build stage.
        if not (project_context_dir / 'Program.cs') in copy_paths:
            raise AssertionError(f"{name}: Program.cs not copied by Dockerfile")
        count=0
        for e in csproj.findall('.//Compile') + csproj.findall('.//ProjectReference'):
            pattern = e.get('Include')
            if not pattern or '$(' in pattern or '%(' in pattern:
                raise AssertionError(f"{name}: unsupported dynamic Include: {pattern}")
            matches = list(proj_path.parent.glob(pattern))
            if not matches:
                raise AssertionError(f"{name}: missing project Compile reference: {pattern}")
            for f in matches:
                resolved = f.resolve()
                if not resolved.is_relative_to(ROOT):
                    raise AssertionError(f"{name}: Compile source escapes repo: {resolved}")
                relative = resolved.relative_to(ROOT / context)
                if relative not in copy_paths:
                    raise AssertionError(f"{name}: Docker build omits .csproj source {relative} (Include={pattern})")
                count+=1
        print(f"OK: {name} Docker compile context ({len(sources)} project C# files, {count} explicitly included files)")
    front_docker = (ROOT/'apps/web/Dockerfile').read_text()
    if 'COPY . .' not in front_docker or 'RUN npm ci' not in front_docker:
        raise AssertionError('React build image must copy source and npm ci')
    for file in ['apps/web/package.json', 'apps/web/package-lock.json']:
        if not (ROOT/file).is_file():
            raise AssertionError(f'Missing React build dependency {file}')
    print('OK: front Docker npm context')


if __name__=='__main__':
    try:
        check()
    except Exception as e:
        print('FAIL:',e,file=sys.stderr)
        sys.exit(1)
