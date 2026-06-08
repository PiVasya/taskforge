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
  printf '%s\n' "$block" | grep -q "python -c" || fail "$file python-runner healthcheck must use Python stdlib, not curl"
  if printf '%s\n' "$block" | grep -q "curl -fsS"; then
    fail "$file python-runner healthcheck uses curl, but python:slim does not include curl"
  fi
done

if [ -d deploy/dev/build-logs ]; then
  fail "deploy/dev/build-logs should not be committed/packed into runnable archives"
fi

echo "runtime config ok"
