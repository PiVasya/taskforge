#!/usr/bin/env python3
"""Guard against a repeat of the cluster-v2 workflow-not-triggered incident."""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/k3s-sandbox-ci.yml'

def check():
    text = WORKFLOW.read_text(encoding='utf-8')
    block = re.search(r'^on:\s*\n\s+push:\s*\n\s+branches:\s*\n((?:\s+- [^\n]+\n)+)', text, flags=re.M)
    assert block, 'K3s workflow must have a push trigger with explicit branches'
    branches = [line.split('-',1)[1].strip().strip("'\"") for line in block.group(1).splitlines()]
    assert set(branches) == {'cluster-v2','k3s-*','k3s/**'}, f'Unexpected push targets: {branches}'
    assert 'develop' not in branches and 'main' not in branches
    job_guards = re.findall(r'^    if: (.+)$', text, flags=re.M)
    assert len(job_guards) == 6, f'Expected six guarded jobs, found {len(job_guards)}'
    for guard in job_guards:
        assert "github.ref == 'refs/heads/cluster-v2'" in guard, guard
        assert "startsWith(github.ref, 'refs/heads/k3s-')" in guard, guard
        assert "startsWith(github.ref, 'refs/heads/k3s/')" in guard, guard
    assert "github.event_name == 'push'" in job_guards[-1], 'Publishing must be restricted to branch pushes'
    legacy = [
        '.github/workflows/develop-build.yml',
        '.github/workflows/develop-full-rebuild.yml',
        '.github/workflows/minecraft-plugins-build.yml',
        'deploy/prod', 'deploy/dev', 'deploy/cluster',
        'services/ai/worker', 'apps/web-ct', 'services/tasks/quiz-api',
    ]
    extra = [str(p) for name in legacy if (p := ROOT/name).exists()]
    assert not extra, f'Legacy files will break CI or compromise isolation: {extra}'
    for name in [
        'infrastructure/k8s/taskforge-yugabyte/db/create-k3s-lab-databases.sql',
        'infrastructure/k8s/taskforge-yugabyte/db/create-databases.sql',
        'tools/k3s/local/generate.mjs',
    ]:
        assert (ROOT/name).is_file(), f'Missing K3s/YSQL source: {name}'
    # The GitHub graph is an explicit release gate, not a collection of independent builds.
    release_gate = 'needs: [k3s-manifests, dotnet-build, frontend, yugabyte-migrations, docker-build-smoke]'
    assert release_gate in text, 'Image publication must depend on source, APIs, React, YSQL and OCI builds'
    for stage in ('start', 'create', 'migrate', 'verify', 'diagnose', 'cleanup'):
        assert f'bash scripts/ci/yugabyte-smoke.sh {stage}' in text, f'Missing YSQL stage: {stage}'
    assert 'name: Upload YugabyteDB diagnostics' in text
    assert 'name: Destroy disposable database\n        if: always()' in text
    assert 'name: Capture database diagnostics on failure\n        if: failure()' in text
    assert 'python3 scripts/ci/test-yugabyte-smoke.py' in text
    assert text.count('runs-on: ubuntu-24.04') == 6, 'All CI runners must use a pinned Ubuntu image'
    assert 'actions/checkout@v4' not in text and 'actions/setup-dotnet@v4' not in text
    assert 'ghcr.io/${{ steps.owner.outputs.owner }}/taskforge-k3s-lab/' in text
    print('PASS: isolated branch triggers, six CI jobs, gated GHCR release, YSQL lifecycle and failure artifacts')

if __name__ == '__main__':
    try: check()
    except (OSError, AssertionError) as exc:
        print('FAIL:', exc, file=sys.stderr)
        sys.exit(1)
