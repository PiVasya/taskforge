#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

printf '\n========== K3S LAB: REPOSITORY AND RELEASE BOUNDARIES ==========\n'
python3 scripts/ci/check-k3s-branch-layout.py
python3 scripts/ci/check-k3s-workflow-trigger.py
python3 scripts/ci/check-k3s-docker-context.py
python3 scripts/ci/check-k3s-migration-contract.py
python3 scripts/ci/check-k3s-release-boundaries.py
python3 scripts/ci/check-database-table-ownership.py
python3 scripts/ci/check-admin-solution-feed.py
bash scripts/security/check-browser-api-security.sh

printf '\n========== K3S LAB: EF MIGRATION HISTORY AND CLI ==========\n'
python3 scripts/ci/check-migration-tooling-safety.py
python3 scripts/ci/test-ef-migration-check.py
python3 scripts/ci/test-generate-migrations-cli.py
for directory in \
  services/identity/api/Migrations \
  services/education/api/Migrations \
  services/content/api/Migrations \
  services/tasks/assignment-api/Migrations \
  services/solutions/api/Migrations \
  services/execution/api/Migrations; do
  python3 scripts/ci/check-ef-migration-history.py --dir "$directory"
done

printf '\n========== K3S LAB: OWNERSHIP TRANSFER AND YSQL CI ==========\n'
python3 scripts/ci/check-attempt-ownership-source.py
python3 scripts/ci/test-yugabyte-smoke.py

printf '\n========== K3S LAB: KUBERNETES GENERATOR ==========\n'
node --test tools/k3s/local/local.test.mjs tools/k3s/local/sandbox.test.mjs

printf '\n========== K3S LAB: COMMON PORTABLE TESTS ==========\n'
python3 scripts/ci/test-ci-error-summary.py
python3 scripts/diagnostics/test-analyze-code-queue.py
bash scripts/ci/test-onec-bootstrap.sh
bash scripts/tests/sql-engine-gate-readiness.sh
for f in infrastructure/k8s/taskforge-yugabyte/local/*.sh scripts/ci/yugabyte-smoke.sh scripts/generate-migrations.sh; do
  bash -n "$f"
done

printf '\nPASS: isolated K3s repository and migration boundary checks passed.\n'
printf 'NOTE: C# and YugabyteDB runtime gates require .NET 10, Docker and a real disposable YSQL instance.\n'
