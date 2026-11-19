#!/usr/bin/env python3
"""Explain exactly why the isolated K3s branch fails the first CI gate."""
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
REQUIRED=(
    'infrastructure/k8s/taskforge-yugabyte/local/run-local.sh',
    'tools/k3s/local/generate.mjs',
    'infrastructure/k8s/taskforge-yugabyte/db/create-databases.sql',
    'infrastructure/k8s/taskforge-yugabyte/db/create-k3s-lab-databases.sql',
    'scripts/ci/check-k3s-workflow-trigger.py',
)
FORBIDDEN=(
    'services/ai/worker',
    'apps/web-ct',
    'services/tasks/quiz-api',
    'deploy/prod',
    'deploy/dev',
    'deploy/cluster',
    'scripts/prod',
    '.github/workflows/develop-build.yml',
    '.github/workflows/develop-full-rebuild.yml',
    '.github/workflows/minecraft-plugins-build.yml',
)

def check():
    missing=[p for p in REQUIRED if not (ROOT/p).is_file()]
    ignore=(ROOT/'.gitignore').read_text(encoding='utf-8')
    for path in ('infrastructure/k8s/taskforge-yugabyte/db/create-databases.sql',
                 'infrastructure/k8s/taskforge-yugabyte/db/create-k3s-lab-databases.sql'):
        if '!'+path not in ignore:
            missing.append('.gitignore must UNIGNORE '+path)
    leftovers=[p for p in FORBIDDEN if (ROOT/p).exists()]
    for p in missing:
        print(f'::error title=Missing sandbox source::{p}',file=sys.stderr)
    for p in leftovers:
        print(f'::error title=Legacy production content in cluster-v2::{p}',file=sys.stderr)
    if missing or leftovers:
        print('This is an incomplete Git branch replacement, NOT a compiler failure.',file=sys.stderr)
        print('Uploading or copying a ZIP over a branch does not delete old Git-tracked files.',file=sys.stderr)
        print('Remove the obsolete tracked files from the Git repository before running CI.',file=sys.stderr)
        return 1
    print('PASS: branch has complete K3s source and NO legacy CT, LLM worker, or production deployment paths')
    return 0

if __name__=='__main__':
    raise SystemExit(check())
