#!/usr/bin/env python3
"""Classify changed paths into independent TaskForge CI test gates.

The classifier intentionally answers *which subsystem needs validation*, not which
Docker images need rebuilding. Normal CI feeds it the pending diff since the last
successful workflow, so a failed subsystem remains fenced without waking unrelated
suites.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def norm(raw: str) -> str:
    return raw.strip().replace('\\', '/').lstrip('./')


def starts(path: str, *prefixes: str) -> bool:
    return any(path == p.rstrip('/') or path.startswith(p) for p in prefixes)


def any_name(path: str, *names: str) -> bool:
    return any(path == n for n in names)


def classify(paths: list[str]) -> dict[str, bool]:
    gates = {
        'frontend': False,
        'minecraft': False,
        'oj': False,
        'browser': False,
        'compose': False,
        'repo_workflow': False,
        'repo_migrations': False,
        'repo_runtime': False,
        'cluster': False,
    }

    for path in paths:
        if not path:
            continue

        if starts(path, 'apps/web/') or any_name(path, 'scripts/tests/frontend.sh'):
            gates['frontend'] = True

        if (
            starts(path, 'services/minecraft/api/')
            or any_name(
                path,
                'apps/web/src/features/settings/SettingsFeature.jsx',
                'apps/web/src/api/minecraftLink.js',
                'scripts/ci/check-minecraft-link-invariants.sh',
            )
            or path == '00_AI_READ_THIS_FIRST.md'
        ):
            gates['minecraft'] = True

        if (
            starts(path, 'services/analyzers/code-analyzer/', 'services/execution/runners/')
            or starts(path, 'tools/csharp-runner-policy-check/')
            or any_name(path, 'scripts/security/check-oj-security.sh')
            or path in {
                'services/execution/worker/Worker.cs',
                'services/execution/worker/Worker.Sanitization.cs',
                'services/execution/api/Services/Results/ExecutionApiResultsService.cs',
                'services/solutions/api/Services/Results/SolutionsApiResultsService.cs',
                'services/solutions/api/Endpoints/Internal/InternalEndpoints.cs',
                'services/tasks/assignment-api/Services/Common/AssignmentApiCommonService.cs',
                'services/tasks/assignment-api/Services/Image/AssignmentApiImageService.cs',
                'deploy/dev/compose/20-core-services.yaml',
                'deploy/dev/compose/30-execution.yaml',
                'deploy/dev/compose/40-ai-and-analyzers.yaml',
                'deploy/prod/compose/20-core-services.yaml',
                'deploy/prod/compose/30-execution.yaml',
                'deploy/prod/compose/40-ai-and-analyzers.yaml',
            }
        ):
            gates['oj'] = True

        if (
            starts(path, 'services/browser/api/', 'tools/browser-session-access-check/', 'tools/browser-url-policy-check/')
            or starts(path, 'apps/gateway/templates/')
            or any_name(path, 'scripts/security/check-browser-api-security.sh', 'scripts/ci/check-browser-api-build.sh', 'scripts/prod/check-browser-api.sh')
            or path in {
                'apps/gateway/99-select-config.sh',
                'apps/gateway/snippets/ai-telemetry-http.conf',
                'apps/gateway/snippets/api-routes.conf',
                'apps/gateway/snippets/proxy-common.conf',
                'apps/gateway/snippets/cloudflare-real-ip.conf',
                'apps/web-ct/src/App.jsx',
                'services/bots/support-bot/AiAccessTelemetry.cs',
                'services/bots/support-bot/Program.cs',
                'services/identity/api/Data/IdentityDbContext.cs',
                'services/identity/api/Domain/IdentityUser.cs',
                'services/identity/api/Endpoints/Auth/AuthEndpoints.cs',
                'services/identity/api/Services/Security/IdentityAuthRateLimiter.cs',
                'services/identity/api/Services/Telemetry/AiAccessTelemetryClient.cs',
                'deploy/prod/compose/10-apps-gateway.yaml',
                'apps/web/src/App.jsx',
                'apps/web/public/index.html',
                'apps/web/src/components/CodeEditor.jsx',
                'apps/web/src/features/assignment-solve/AssignmentSolveFeature.jsx',
                'apps/web/src/features/assignment-solve/components/SolveDraftEditor.jsx',
                'apps/web/src/features/attempts/recoverSubmittedAttempt.js',
                'apps/web/src/features/math-task/MathTaskBlock.jsx',
                'apps/web/src/features/task-test/TaskTestQuestion.jsx',
                'apps/web/src/pages/MathTaskSolve.jsx',
                'apps/web/src/pages/TaskTestSolve.jsx',
                'apps/web/src/features/course-assignments/nodes/CourseMapNodePrimitives.jsx',
                'apps/web/src/features/course-assignments/nodes/CourseNode.jsx',
                'apps/web/src/features/course-assignments/nodes/CodeTestNode.jsx',
                'apps/web/src/features/course-assignments/nodes/ImageCodeNode.jsx',
                'apps/web/src/features/course-assignments/nodes/MathNode.jsx',
                'apps/web/src/features/course-assignments/nodes/TestNode.jsx',
            }
        ):
            gates['browser'] = True

        if (
            starts(path, 'deploy/dev/compose/', 'deploy/prod/compose/')
            or path in {
                'deploy/dev/.env.example',
                'deploy/prod/.env.example',
                'deploy/dev/compose.sh',
                'deploy/prod/compose.sh',
                'scripts/tests/compose.sh',
            }
        ):
            gates['compose'] = True

        if (
            starts(path, '.github/workflows/')
            or path.endswith('/Dockerfile') or path == 'Dockerfile'
            or starts(path, 'deploy/prod/compose/', 'deploy/cluster/compose.cluster.yaml')
            or any_name(
                path,
                'scripts/ci/check-workflow-integrity.py',
                'scripts/ci/check-docker-build-contexts.py',
                'scripts/ci/check-workflow-matrix.sh',
                'scripts/ci/test-change-impact.py',
                'scripts/ci/test-ci-change-impact.py',
                'scripts/ci/dotnet-change-matrix.py',
                'scripts/ci/sql-change-impact.py',
                'scripts/ci/test-sql-change-impact.py',
                'scripts/tests/repository.sh',
            )
        ):
            gates['repo_workflow'] = True

        if (
            '/Migrations/' in f'/{path}'
            or path.endswith('ModelSnapshot.cs')
            or any_name(
                path,
                'scripts/generate-migrations.sh',
                'scripts/generate-migrations-force.sh',
                'scripts/ci/check-migration-tooling-safety.py',
            )
        ):
            gates['repo_migrations'] = True

        if (
            starts(path, 'deploy/dev/compose/', 'deploy/prod/compose/')
            or path in {'deploy/dev/.env.example', 'deploy/prod/.env.example'}
            or path.endswith('/Dockerfile') or path == 'Dockerfile'
            or path.endswith('/Program.cs')
            or path.endswith('/Diagnostics/TaskForgeDebugDiagnostics.cs')
            or path in {
                'services/execution/sql-worker/cmd/sql-worker/main.go',
                'services/analyzers/image-analyzer/Dockerfile',
                'services/analyzers/code-analyzer/Dockerfile',
            }
            or any_name(
                path,
                'scripts/ci/check-runtime-config-boundaries.py',
                'scripts/ci/check-runtime-logging.py',
                'scripts/prod/prepare-env.sh',
                'scripts/prod/check-prod-config.sh',
            )
        ):
            gates['repo_runtime'] = True

        if (
            starts(path, 'deploy/cluster/')
            or starts(path, 'services/observability/api/Services/Cluster/')
            or path == 'services/observability/api/Endpoints/SystemStatus/SystemStatusEndpoints.cs'
            or path == 'services/observability/api/Diagnostics/TaskForgeDebugDiagnostics.cs'
            or starts(path, 'apps/web/src/features/cluster/')
            or path in {
                'apps/web/src/api/systemStatus.js',
                'apps/web/src/pages/admin/AdminSystemStatusPage.jsx',
                'apps/gateway/snippets/api-routes.conf',
                'deploy/prod/compose/00-storage.yaml',
                'deploy/prod/compose/50-integrations.yaml',
                'deploy/prod/compose/80-watchtower.yaml',
                'deploy/prod/compose.sh',
                'deploy/prod/deploy.sh',
                'deploy/prod/bootstrap.sh',
                'deploy/prod/cluster.sh',
                'scripts/ci/check-cluster-diagnostics-control.py',
                'scripts/ci/check-cluster-ha.sh',
                'scripts/ci/test-cluster-runtime.py',
                'scripts/ci/test-easy-cluster.py',
                'scripts/ci/test-ha-agent-state.py',
                'scripts/check-cluster-control-v40.sh',
            }
        ):
            gates['cluster'] = True

    return gates


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--field', choices=[
        'frontend', 'minecraft', 'oj', 'browser', 'compose',
        'repo_workflow', 'repo_migrations', 'repo_runtime', 'cluster',
    ])
    parser.add_argument('--json', action='store_true')
    parser.add_argument('--explain', action='store_true')
    args = parser.parse_args()

    paths = [norm(line) for line in sys.stdin.read().splitlines() if norm(line)]
    gates = classify(paths)

    if args.field:
        print('true' if gates[args.field] else 'false')
        return 0
    if args.json:
        print(json.dumps(gates, sort_keys=True, separators=(',', ':')))
        return 0
    if args.explain:
        selected = [name for name, value in gates.items() if value]
        print('selected=' + (','.join(selected) if selected else '<none>'))
        return 0

    for name, value in gates.items():
        print(f'{name}={str(value).lower()}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
