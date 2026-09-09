#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
python3 ./scripts/ci/check-sql-runtime.py
python3 ./scripts/ci/check-csharp-source-invariants.py
python3 ./scripts/ci/check-workflow-integrity.py
python3 ./scripts/ci/check-docker-build-contexts.py
bash ./scripts/check-sql-domain.sh
# Domain checks build the three changed APIs. Education also projects SQL course-map nodes.
dotnet build ./services/education/api/TaskForge.Education.Api.csproj -c Release
bash ./scripts/check-sql-go.sh
(
  cd ./apps/web
  npm ci
  node scripts/check-architecture.js
  node --test scripts/sql-tests/*.test.mjs
  npm run test:cluster
  npm run build
)
printf '\nPASS: source build/domain/Go/SQLite/frontend gate.\n'
printf 'Next real engine gate: bash ./scripts/sql/test-engines.sh\n'
printf 'No production database was accessed. No migrations were generated/applied.\n'
