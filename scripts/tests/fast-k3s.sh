#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

failures=()
run_suite() {
  local name="$1"
  shift
  printf '\n========== K3S FAST: %s ==========\n' "$name"
  if "$@"; then
    printf 'PASS: %s\n' "$name"
  else
    local result=$?
    failures+=("$name (exit $result)")
    printf 'FAIL: %s (exit %d)\n' "$name" "$result" >&2
  fi
}

run_suite 'Repository, ownership, YSQL mock and K3s manifests' bash scripts/tests/repository-k3s.sh
run_suite 'Six API builds and behavior' bash scripts/tests/dotnet-k3s.sh
run_suite 'Frontend architecture' node apps/web/scripts/check-architecture.js
run_suite 'Frontend node model behavior' bash -c 'cd apps/web && node --test scripts/*/*.test.mjs'
run_suite 'Native SQL worker Go/security gate' bash scripts/check-sql-go.sh

if (( ${#failures[@]} > 0 )); then
  printf '\nK3S FAST FAILED: %d gate(s)\n' "${#failures[@]}" >&2
  printf '  %s\n' "${failures[@]}" >&2
  exit 1
fi
printf '\nPASS: K3s fast source, frontend, API and native SQL gates.\n'
printf 'Real YugabyteDB/K3s and React production build still require the full integration gates.\n'
