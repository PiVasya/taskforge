#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT/apps/web"

for tool in node npm; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "ERROR: $tool is required for TaskForge frontend tests." >&2
    exit 2
  fi
done

if [ "${TASKFORGE_SKIP_NPM_CI:-0}" != "1" ]; then
  npm ci
  npm --prefix "$ROOT/apps/web-ct" ci
else
  for app in web web-ct; do
    if [ ! -x "$ROOT/apps/$app/node_modules/.bin/react-scripts" ]; then
      echo "ERROR: $app dependencies are missing. Run: npm --prefix apps/$app ci" >&2
      exit 2
    fi
  done
fi

printf '\n========== FRONTEND ARCHITECTURE ==========\n'
node scripts/check-architecture.js

printf '\n========== FRONTEND MODEL TESTS ==========\n'
node --test scripts/auth-tests/*.test.mjs
node --test scripts/sql-tests/*.test.mjs
node --test scripts/agent-tests/*.test.mjs
node --test scripts/admin-solutions-tests/*.test.mjs
node --test scripts/leaderboard-tests/*.test.mjs
node --test scripts/landing-tests/*.test.mjs
node --test scripts/frontend-diagnostics-tests/*.test.mjs
node --test scripts/course-map-tests/*.test.mjs
node --test scripts/cluster-tests/*.test.mjs

printf '\n========== FRONTEND JEST TESTS ==========\n'
CI=true npm test -- --watchAll=false --runInBand

printf '\n========== FRONTEND PRODUCTION BUILD ==========\n'
CI=true npm run build

printf '\n========== CT FRONTEND ARCHITECTURE ==========\n'
cd "$ROOT/apps/web-ct"
node scripts/check-architecture.js

printf '\n========== CT FRONTEND JEST TESTS ==========\n'
CI=true npm test -- --watchAll=false --runInBand

printf '\n========== CT FRONTEND PRODUCTION BUILD ==========\n'
CI=true npm run build

printf '\nPASS: both frontend test suites and production builds passed.\n'
