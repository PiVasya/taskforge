#!/usr/bin/env python3
"""Fail fast when Dockerfiles, prod compose images and Docker build matrices drift apart."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "develop-build.yml"
FULL_REBUILD_WORKFLOW = ROOT / ".github" / "workflows" / "develop-full-rebuild.yml"
PROD_COMPOSE = ROOT / "deploy" / "prod" / "compose"
CLUSTER_COMPOSE = ROOT / "deploy" / "cluster" / "compose.cluster.yaml"


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


def full_rebuild_matrix_entries() -> dict[str, str]:
    text = FULL_REBUILD_WORKFLOW.read_text(encoding="utf-8")
    entries: dict[str, str] = {}
    pattern = re.compile(
        r'^\s*- name: ([^\n]+)\n\s*context: ([^\n]+)\n\s*dockerfile: ([^\n]+)$',
        re.MULTILINE,
    )
    for name, context, dockerfile in pattern.findall(text):
        context_path = Path(context.strip().replace("./", "", 1))
        full = (ROOT / context_path / dockerfile.strip()).resolve()
        entries[name.strip()] = norm(full.relative_to(ROOT))
    return entries


def project_dockerfiles() -> set[str]:
    # The integrity check is about TaskForge-owned build inputs. A developer may
    # legitimately have npm/.NET/test outputs in the working tree after running
    # the canonical suites; Dockerfiles shipped by dependencies or generated
    # outputs must not suddenly become "missing" workflow images.
    ignored_parts = {
        ".git",
        ".cache",
        ".venv",
        "venv",
        "node_modules",
        "bin",
        "obj",
        "build",
        "dist",
        "coverage",
        "__pycache__",
    }
    files = set()
    for path in ROOT.rglob("Dockerfile"):
        relative = path.relative_to(ROOT)
        if any(part in ignored_parts for part in relative.parts[:-1]):
            continue
        rel = norm(relative)
        files.add(rel)
    return files


def prod_images() -> set[str]:
    names: set[str] = set()
    paths = list(sorted(PROD_COMPOSE.glob("*.y*ml")))
    if CLUSTER_COMPOSE.is_file():
        paths.append(CLUSTER_COMPOSE)
    for path in paths:
        text = path.read_text(encoding="utf-8")
        for image in re.findall(r'image:\s*\$\{IMAGE_REPOSITORY[^}]*\}/([^:\s]+):\$\{IMAGE_TAG', text):
            names.add(image)
    return names


def main() -> int:
    matrix = matrix_entries()
    full_rebuild_matrix = full_rebuild_matrix_entries()
    dockerfiles = project_dockerfiles()
    prod = prod_images()

    errors: list[str] = []
    matrix_dockerfiles = set(matrix.values())

    if matrix != full_rebuild_matrix:
        normal_only = sorted(set(matrix) - set(full_rebuild_matrix))
        manual_only = sorted(set(full_rebuild_matrix) - set(matrix))
        changed = sorted(
            name for name in set(matrix) & set(full_rebuild_matrix)
            if matrix[name] != full_rebuild_matrix[name]
        )
        details: list[str] = []
        if normal_only:
            details.append("Only in develop-build.yml: " + ", ".join(normal_only))
        if manual_only:
            details.append("Only in develop-full-rebuild.yml: " + ", ".join(manual_only))
        if changed:
            details.append("Different Dockerfiles: " + ", ".join(changed))
        errors.append(
            "Full-rebuild workflow matrix does not match develop-build matrix:\n  "
            + "\n  ".join(details)
        )

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

    normal_workflow_text = WORKFLOW.read_text(encoding="utf-8")
    sql_engine_gate_text = (ROOT / "scripts/sql/test-engines.sh").read_text(encoding="utf-8")
    sql_dockerfile_text = (ROOT / "services/execution/sql-worker/Dockerfile").read_text(encoding="utf-8")

    if 'forcing a full rebuild to avoid stale images' in normal_workflow_text:
        errors.append("develop-build.yml may not turn a missing baseline into an implicit full rebuild")
    if 'Workflow file changed; forcing full rebuild' in normal_workflow_text:
        errors.append("develop-build.yml may not rebuild every image merely because the workflow file changed")
    if 'default: true' in re.search(r"build_all:.*?(?=\n      [a-z_]+:|\nconcurrency:)", normal_workflow_text, re.DOTALL).group(0):
        errors.append("normal workflow build_all must be opt-in; use the dedicated full-rebuild workflow")
    if 'docker build ' in sql_engine_gate_text or 'docker buildx build ' in sql_engine_gate_text:
        errors.append("real SQL engine tests must not build a TaskForge Docker image")
    if "-run '^TestRealEngine'" not in sql_engine_gate_text or "-run '^TestRealRabbitWakeup$'" not in sql_engine_gate_text:
        errors.append("real SQL engine gate must run only the provider-specific integration tests")
    if 'FROM build AS integration-build' not in sql_dockerfile_text:
        errors.append("sql-worker release build must not compile integration test binaries in its shared build stage")

    sql_update_text = (ROOT / "scripts/check-sql-update.sh").read_text(encoding="utf-8")
    all_tests_text = (ROOT / "scripts/tests/all.sh").read_text(encoding="utf-8")
    if 'TASKFORGE_SQL_GO_COVERED_BY_REAL_ENGINE_GATE' in sql_update_text or 'TASKFORGE_SQL_GO_COVERED_BY_REAL_ENGINE_GATE' in all_tests_text:
        errors.append("SQL Go/SQLite gate may not be skipped in favor of the provider-only real-engine suite")

    # Normal CI must path-gate independent suites. Full rebuild intentionally stays exhaustive.
    required_outputs = (
        'dotnet_matrix', 'dotnet_count', 'frontend', 'minecraft', 'oj', 'browser',
        'compose', 'repo_workflow', 'repo_migrations', 'repo_runtime', 'cluster',
        'sql_contract', 'sql_engines',
    )
    for output in required_outputs:
        if f'{output}: ${{{{ steps.matrix.outputs.{output} }}}}' not in normal_workflow_text:
            errors.append(f'normal workflow changes job does not expose independent test output: {output}')

    for classifier in ('scripts/ci/test-change-impact.py', 'scripts/ci/dotnet-change-matrix.py', 'scripts/ci/sql-change-impact.py'):
        if classifier not in normal_workflow_text:
            errors.append(f'normal workflow does not use required change-impact classifier: {classifier}')

    normal_jobs = {
        'workflow-integrity': "needs.changes.outputs.repo_workflow == 'true'",
        'migration-safety': "needs.changes.outputs.repo_migrations == 'true'",
        'runtime-config-invariants': "needs.changes.outputs.repo_runtime == 'true'",
        'cluster-invariants': "needs.changes.outputs.cluster == 'true'",
        'dotnet-behavior-tests': "needs.changes.outputs.dotnet_count != '0'",
        'frontend-tests': "needs.changes.outputs.frontend == 'true'",
        'minecraft-link-invariants': "needs.changes.outputs.minecraft == 'true'",
        'oj-security-invariants': "needs.changes.outputs.oj == 'true'",
        'browser-security-invariants': "needs.changes.outputs.browser == 'true'",
        'sql-contract-check': "needs.changes.outputs.sql_contract == 'true'",
        'sql-engine-check': "needs.changes.outputs.sql_engines == 'true'",
        'compose-check': "needs.changes.outputs.compose == 'true'",
    }
    for job_name, condition in normal_jobs.items():
        job_match = re.search(
            rf'^  {re.escape(job_name)}:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9-]*:|\Z)',
            normal_workflow_text,
            re.MULTILINE | re.DOTALL,
        )
        if job_match is None:
            errors.append(f'develop-build.yml misses independent test job: {job_name}')
            continue
        if condition not in job_match.group('body'):
            errors.append(f'develop-build.yml {job_name} is not gated by its own impact output')

    dotnet_job = re.search(
        r'^  dotnet-behavior-tests:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9-]*:|\Z)',
        normal_workflow_text,
        re.MULTILINE | re.DOTALL,
    )
    if dotnet_job:
        body = dotnet_job.group('body')
        if 'fromJson(needs.changes.outputs.dotnet_matrix)' not in body:
            errors.append('normal .NET tests must use an affected-project matrix')
        if 'bash scripts/tests/dotnet-project.sh' not in body:
            errors.append('normal .NET tests must build/test only the affected service project')
        if 'bash scripts/tests/dotnet.sh' in body:
            errors.append('normal .NET job may not rebuild every .NET service for one project change')

    sql_contract_job = re.search(
        r'^  sql-contract-check:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9-]*:|\Z)',
        normal_workflow_text,
        re.MULTILINE | re.DOTALL,
    )
    if sql_contract_job:
        body = sql_contract_job.group('body')
        if 'bash ./scripts/check-sql-domain.sh' not in body or 'test-engines.sh' in body:
            errors.append('SQL contract job must stay independent from real engine containers')

    sql_engine_job = re.search(
        r'^  sql-engine-check:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9-]*:|\Z)',
        normal_workflow_text,
        re.MULTILINE | re.DOTALL,
    )
    if sql_engine_job:
        body = sql_engine_job.group('body')
        for command in ('bash ./scripts/check-sql-go.sh', 'bash ./scripts/sql/test-engines.sh'):
            if command not in body:
                errors.append(f'SQL engine job misses canonical provider check: {command}')

    build_match = re.search(r'^  build:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9-]*:|\Z)', normal_workflow_text, re.MULTILINE | re.DOTALL)
    if not build_match:
        errors.append('develop-build.yml has no build job')
    else:
        build_body = build_match.group('body')
        for job_name in normal_jobs:
            if job_name not in build_body:
                errors.append(f'develop-build.yml build does not wait for optional gate: {job_name}')
            if f"needs.{job_name}.result == 'skipped'" not in build_body:
                errors.append(f'develop-build.yml build does not allow intentionally skipped gate: {job_name}')

    # The manual full-rebuild workflow is intentionally exhaustive and keeps the canonical full suites.
    full_text = FULL_REBUILD_WORKFLOW.read_text(encoding='utf-8')
    full_required = {
        'workflow-integrity': 'bash scripts/tests/repository.sh',
        'dotnet-behavior-tests': 'bash scripts/tests/dotnet.sh',
        'frontend-tests': 'bash scripts/tests/frontend.sh',
        'minecraft-link-invariants': 'bash scripts/ci/check-minecraft-link-invariants.sh',
        'oj-security-invariants': 'bash scripts/security/check-oj-security.sh',
        'browser-security-invariants': 'bash scripts/security/check-browser-api-security.sh',
        'sql-runtime-check': 'bash ./scripts/check-sql-update.sh',
        'compose-check': 'bash scripts/tests/compose.sh',
    }
    for job_name, command in full_required.items():
        job_match = re.search(
            rf'^  {re.escape(job_name)}:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9-]*:|\Z)',
            full_text,
            re.MULTILINE | re.DOTALL,
        )
        if job_match is None:
            errors.append(f'develop-full-rebuild.yml misses exhaustive job: {job_name}')
        elif command not in job_match.group('body'):
            errors.append(f'develop-full-rebuild.yml {job_name} does not run canonical suite: {command}')

    for workflow_path in (WORKFLOW, FULL_REBUILD_WORKFLOW):
        workflow_text = workflow_path.read_text(encoding='utf-8')
        if 'check-csharp-source-invariants.py' in workflow_text or 'check-authoring-regressions.py' in workflow_text:
            errors.append(f'{workflow_path.name} still runs a retired source-grep regression checker')

    if errors:
        print("\n\n".join(errors), file=sys.stderr)
        return 1

    print(
        f"Workflow integrity OK: {len(matrix)} matrix images, {len(dockerfiles)} Dockerfiles, "
        f"{len(prod)} prod images, full-rebuild matrix aligned."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
