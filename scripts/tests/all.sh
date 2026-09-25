#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

failures=()
run_suite() {
  local label="$1"
  shift
  printf '\n================================================================================\n'
  printf 'SUITE: %s\n' "$label"
  printf '================================================================================\n'
  if "$@"; then
    printf '\nPASS SUITE: %s\n' "$label"
  else
    local rc=$?
    failures+=("$label (exit $rc)")
    printf '\nFAIL SUITE: %s (exit %d)\n' "$label" "$rc" >&2
  fi
}

run_suite 'Repository / release boundaries' bash scripts/tests/repository.sh
run_suite '.NET production build + behavior' bash scripts/tests/dotnet.sh
run_suite 'Frontend behavior + production build' bash scripts/tests/frontend.sh
run_suite 'Minecraft link invariants' bash scripts/ci/check-minecraft-link-invariants.sh
run_suite 'OJ security' bash scripts/security/check-oj-security.sh
run_suite 'Browser API security' bash scripts/security/check-browser-api-security.sh
run_suite 'Compose validation' bash scripts/tests/compose.sh
run_suite 'SQL domain / update gate' bash scripts/check-sql-update.sh
run_suite 'SQL real engines' bash scripts/sql/test-engines.sh

if [ "${#failures[@]}" -ne 0 ]; then
  printf '\n================================================================================\n' >&2
  printf 'TASKFORGE TEST SUITE FAILED: %d suite(s) failed\n' "${#failures[@]}" >&2
  printf '  - %s\n' "${failures[@]}" >&2
  printf '================================================================================\n' >&2
  exit 1
fi

printf '\nPASS: full TaskForge test suite passed.\n'
