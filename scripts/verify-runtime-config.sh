#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

fail() {
  echo "error: $*" >&2
  exit 1
}

check_file() {
  [ -f "$1" ] || fail "missing file: $1"
}

check_file deploy/dev/compose/00-storage.yaml
check_file deploy/prod/compose/00-storage.yaml
check_file deploy/dev/compose/30-execution.yaml
check_file deploy/prod/compose/30-execution.yaml

if grep -R "/var/lib/postgresql/data" deploy/dev/compose deploy/prod/compose >/dev/null 2>&1; then
  grep -R "/var/lib/postgresql/data" -n deploy/dev/compose deploy/prod/compose >&2 || true
  fail "PostgreSQL 18 compose files must mount postgres-data to /var/lib/postgresql, not /var/lib/postgresql/data"
fi

grep -q "postgres-data:/var/lib/postgresql" deploy/dev/compose/00-storage.yaml \
  || fail "dev postgres volume must mount to /var/lib/postgresql"
grep -q "postgres-data:/var/lib/postgresql" deploy/prod/compose/00-storage.yaml \
  || fail "prod postgres volume must mount to /var/lib/postgresql"

grep -q "image: .*postgres:18-alpine" deploy/dev/compose/00-storage.yaml \
  || fail "dev PostgreSQL default image must be postgres:18-alpine"
grep -q "image: .*postgres:18-alpine" deploy/prod/compose/00-storage.yaml \
  || fail "prod PostgreSQL default image must be postgres:18-alpine"

grep -q "../../../infrastructure/postgres/init:/docker-entrypoint-initdb.d:ro" deploy/dev/compose/00-storage.yaml \
  || fail "dev postgres init mount path should point to ../../../infrastructure/postgres/init"
grep -q "../../../infrastructure/postgres/init:/docker-entrypoint-initdb.d:ro" deploy/prod/compose/00-storage.yaml \
  || fail "prod postgres init mount path should point to ../../../infrastructure/postgres/init"

for file in deploy/dev/compose/30-execution.yaml deploy/prod/compose/30-execution.yaml; do
  block="$(awk '/^  python-runner:/{flag=1} /^  image-cpp-runner:/{flag=0} flag{print}' "$file")"
  printf '%s\n' "$block" | grep -q "python-runner" || fail "$file missing python-runner service"
  printf '%s\n' "$block" | grep -q "/health" || fail "$file python-runner healthcheck must call /health"
done

if [ "${TASKFORGE_PACKAGING_CHECK:-0}" = "1" ] && [ -d deploy/dev/build-logs ]; then
  fail "deploy/dev/build-logs should not be committed/packed into runnable archives"
fi

for file in deploy/dev/compose/30-execution.yaml deploy/prod/compose/30-execution.yaml; do
  grep -q "runner-net:" "$file" || fail "$file must define isolated runner-net"
  grep -q "internal: true" "$file" || fail "$file runner-net must be internal"
  awk '/^x-runner-security:/{flag=1} /^services:/{flag=0} flag{print}' "$file" | grep -q "runner-net" \
    || fail "$file runner security anchor must attach runners to runner-net"
  awk '/^  execution-worker:/{flag=1} /^  csharp-runner:/{flag=0} flag{print}' "$file" | grep -q "runner-net" \
    || fail "$file execution-worker must be attached to runner-net"
done

echo "runtime config ok"
