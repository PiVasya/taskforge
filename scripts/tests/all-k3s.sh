#!/usr/bin/env bash
# Full CI-equivalent disposable K3s CORE source and YSQL gate, not a live K3s install.
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

bash scripts/tests/fast-k3s.sh
bash scripts/tests/frontend.sh

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
  echo 'ERROR: a working Docker daemon is required for the disposable YugabyteDB gate.' >&2
  exit 2
fi

# Use only a dedicated disposable CI container, never the existing K3s DB/PVC.
export ASPNETCORE_ENVIRONMENT=Development
export Cluster__MultiNode=true
export Database__RequireYugabyte=true
export Database__MigrationJobOnly=true
export Database__MigrateOnStartup=true
export Database__EnsureCreated=false
export Seed__InitialCatalog=false
export Jwt__Key='taskforge-k3s-local-smoke-test-key-never-for-production'
export Jwt__SigningKey="$Jwt__Key"
export Jwt__Issuer=TaskForgeK3sLab
export Jwt__Audience=TaskForgeK3sLab
export InternalApi__Key='taskforge-k3s-smoke-test-internal-key-never-for-production'

cleanup_ci_db() {
  local rc=$?
  trap - EXIT
  if ! bash scripts/ci/yugabyte-smoke.sh cleanup; then
    printf 'ERROR: could not safely clean up owned lab database container.\n' >&2
    exit 1
  fi
  exit "$rc"
}
# Install cleanup only after a successful start: a refused pre-existing
# disposable container must not be deleted by our error-handling trap.
bash scripts/ci/yugabyte-smoke.sh start
trap cleanup_ci_db EXIT
bash scripts/ci/yugabyte-smoke.sh create
bash scripts/ci/yugabyte-smoke.sh migrate
bash scripts/ci/yugabyte-smoke.sh verify

printf '\nPASS: full local core K3s source, frontend and disposable YugabyteDB smoke gates.\n'
printf 'Live K3s deploy, end-to-end assignment submission and optional services are still separate.\n'
