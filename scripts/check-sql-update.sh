#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

printf '\n========== SQL SOURCE / IMMUTABILITY BOUNDARIES ==========\n'
python3 ./scripts/ci/check-sql-runtime.py

printf '\n========== SQL DOMAIN / EF MODEL ==========\n'
bash ./scripts/check-sql-domain.sh

# Education projects SQL course-map nodes; AI authors SQL assignments.
printf '\n========== SQL-ADJACENT .NET BUILDS ==========\n'
dotnet build ./services/education/api/TaskForge.Education.Api.csproj -c Release --nologo
dotnet build ./services/ai/api/TaskForge.Ai.Api.csproj -c Release --nologo

printf '\n========== GO / SQLITE RUNTIME ==========\n'
sql_go_deferred=0
if [ "${TASKFORGE_SQL_GO_COVERED_BY_REAL_ENGINE_GATE:-0}" = "1" ]; then
  sql_go_deferred=1
  printf 'DEFERRED: host native Go/SQLite gate is covered by the immediately following Docker real-engine suite.\n'
else
  bash ./scripts/check-sql-go.sh
fi

printf '\n========== SQL FRONTEND MODEL CONTRACTS ==========\n'
node ./apps/web/scripts/sql-tests/model.test.mjs

if [ "$sql_go_deferred" = "1" ]; then
  printf '\nPASS: SQL source/domain/frontend-model gate; Go/SQLite execution is deferred to the real-engine Docker suite.\n'
else
  printf '\nPASS: SQL source/domain/Go/SQLite/frontend-model gate.\n'
fi
printf 'Real engine gate: bash ./scripts/sql/test-engines.sh\n'
printf 'Full frontend gate: bash ./scripts/tests/frontend.sh\n'
printf 'No production database was accessed. No migrations were generated/applied.\n'
