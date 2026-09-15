#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

failures=()
run_suite() {
  local label="$1"
  shift
  printf '\n========== %s ==========\n' "$label"
  if "$@"; then
    printf 'PASS SUITE: %s\n' "$label"
  else
    local rc=$?
    failures+=("$label (exit $rc)")
    printf 'FAIL SUITE: %s (exit %d)\n' "$label" "$rc" >&2
  fi
}

run_suite 'Repository / release boundaries' bash scripts/tests/repository.sh
run_suite '.NET production build + behavior' bash scripts/tests/dotnet.sh
run_suite 'OJ security' bash scripts/security/check-oj-security.sh
run_suite 'Browser API security' bash scripts/security/check-browser-api-security.sh
run_suite 'SQL Go/native' bash scripts/check-sql-go.sh

if [ "${#failures[@]}" -ne 0 ]; then
  printf '\nFAST TASKFORGE SUITE FAILED: %d suite(s) failed\n' "${#failures[@]}" >&2
  printf '  - %s\n' "${failures[@]}" >&2
  exit 1
fi

printf '\nPASS: fast cross-project test suite passed.\n'
printf 'Docker-backed SQL integration remains separate: bash scripts/sql/test-engines.sh\n'
printf 'Frontend production suite remains separate: bash scripts/tests/frontend.sh\n'
