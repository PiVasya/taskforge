#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/services/execution/sql-worker"
for tool in go gcc pkg-config; do
  command -v "$tool" >/dev/null || { echo "Missing $tool; see docs/sql/GO_RUNTIME.md" >&2; exit 2; }
done
[ "$(go env GOOS)" = linux ] || { echo 'The SQL helper security gate requires Linux.' >&2; exit 2; }
pkg-config --exists libpq libmariadb sqlite3 || { echo 'Install libpq, MariaDB Connector/C and SQLite development packages.' >&2; exit 2; }
export CGO_ENABLED=1 GOTOOLCHAIN=local
printf 'Go: '; go version
pkg-config --modversion libpq libmariadb sqlite3
[ -z "$(gofmt -l cmd internal)" ] || { echo 'Go files require gofmt.' >&2; exit 1; }
go vet ./...
SQL_RUN_LOAD_TESTS=1 go test -race -count=1 -v ./...
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
go build -trimpath -buildvcs=false -ldflags='-s -w' -o "$TMP/sql-worker" ./cmd/sql-worker
"$TMP/sql-worker" version
"$TMP/sql-worker" self-test-isolation
printf '\nPASS: Go build/vet/race, isolated SQLite, lifecycle and 100 Run + 100 Check gate.\n'
printf 'PostgreSQL/MySQL/RabbitMQ integration is separate: bash ./scripts/sql/test-engines.sh\n'
printf 'No migrations or production database writes were performed.\n'
