#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / 'scripts/ci/test-change-impact.py'
spec = importlib.util.spec_from_file_location('impact', MODULE_PATH)
impact = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(impact)


def gate(path: str, name: str) -> bool:
    return impact.classify([path])[name]


def expect(path: str, **expected: bool) -> None:
    actual = impact.classify([path])
    for name, value in expected.items():
        assert actual[name] is value, (path, name, actual[name], value)


expect('apps/web/src/features/landing/landing.css', frontend=True, oj=False, browser=False, compose=False, cluster=False)
expect('services/support/api/Program.cs', frontend=False, oj=False, browser=False, compose=False, repo_runtime=True)
expect('services/bots/support-bot/Program.cs', browser=True, frontend=False, oj=False, repo_runtime=True)
expect('services/bots/support-bot/TelegramSupportService.cs', browser=False, frontend=False, oj=False)
expect('services/analyzers/code-analyzer/src/main.rs', oj=True, browser=False, frontend=False)
expect('services/solutions/api/Program.cs', oj=False, browser=False, repo_runtime=True)
expect('services/solutions/api/Services/Results/SolutionsApiResultsService.cs', oj=True, browser=False)
expect('services/execution/runners/python-runner/main.go', oj=True, frontend=False)
expect('services/browser/api/Program.cs', browser=True, frontend=False, oj=False, repo_runtime=True)
expect('services/identity/api/Services/Users/ProfileService.cs', browser=False, frontend=False, oj=False)
expect('services/minecraft/api/Endpoints/Links/LinksEndpoints.cs', minecraft=True, frontend=False)
expect('deploy/prod/compose/30-execution.yaml', compose=True, repo_runtime=True, oj=True)
expect('deploy/cluster/agent.py', cluster=True, compose=False, frontend=False)
expect('deploy/prod/deploy.sh', cluster=True, compose=False, repo_runtime=False)
expect('deploy/prod/compose/50-integrations.yaml', cluster=True, compose=True, repo_runtime=True)
expect('.github/workflows/develop-build.yml', repo_workflow=True, frontend=False, compose=False)
expect('scripts/generate-migrations.sh', repo_migrations=True)
expect('services/identity/api/Migrations/X.cs', repo_migrations=True, browser=False)
expect('README.md', frontend=False, minecraft=False, oj=False, browser=False, compose=False, repo_workflow=False, repo_migrations=False, repo_runtime=False, cluster=False)


def dotnet(paths: list[str]) -> set[str]:
    proc = subprocess.run(
        [sys.executable, str(ROOT / 'scripts/ci/dotnet-change-matrix.py')],
        input='\n'.join(paths) + '\n', text=True, capture_output=True, check=True,
    )
    import json
    return {item['project'] for item in json.loads(proc.stdout)['include']}

support = dotnet(['services/support/api/Program.cs'])
assert support == {'services/support/api/TaskForge.Support.Api.csproj'}, support
shared_sql = dotnet(['services/shared/Sql/SqlContracts.cs'])
assert shared_sql == {
    'services/execution/api/TaskForge.Execution.Api.csproj',
    'services/solutions/api/TaskForge.Solutions.Api.csproj',
    'services/tasks/assignment-api/TaskForge.Tasks.Api.csproj',
}, shared_sql
frontend = dotnet(['apps/web/src/App.jsx'])
assert not frontend, frontend
all_dotnet = dotnet(['Directory.Build.props'])
assert len(all_dotnet) >= 15, len(all_dotnet)

# dotnet-project.sh relies on colocated test projects compiling the production
# service through ProjectReference, so keep that optimization mechanically true.
for project in sorted((ROOT / 'services').rglob('*.csproj')):
    rel = project.relative_to(ROOT).as_posix()
    if '/tests/' in '/' + rel + '/':
        continue
    test_root = project.parent / 'tests'
    if not test_root.is_dir():
        continue
    for test_project in test_root.rglob('*Tests.csproj'):
        text = test_project.read_text(encoding='utf-8', errors='ignore')
        assert '<ProjectReference Include=' in text, test_project
        assert project.name in text, (test_project, project)

print('test change-impact invariants ok')
