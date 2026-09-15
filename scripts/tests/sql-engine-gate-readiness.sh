#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/scripts/sql/engine-gate-readiness.sh"

marker=abcdef
if sql_gate_final_daemons_ready bash bash "$marker" "$marker" "$marker"; then
  echo 'FAIL: temporary init daemons were accepted as final providers.' >&2
  exit 1
fi
if sql_gate_final_daemons_ready postgres bash "$marker" "$marker" "$marker"; then
  echo 'FAIL: temporary MySQL init daemon was accepted.' >&2
  exit 1
fi
if sql_gate_final_daemons_ready bash mysqld "$marker" "$marker" "$marker"; then
  echo 'FAIL: temporary PostgreSQL init daemon was accepted.' >&2
  exit 1
fi
if sql_gate_final_daemons_ready postgres mysqld wrong "$marker" "$marker"; then
  echo 'FAIL: wrong PostgreSQL guard marker was accepted.' >&2
  exit 1
fi
if ! sql_gate_final_daemons_ready postgres mysqld "$marker" "$marker" "$marker"; then
  echo 'FAIL: final providers with valid guard markers were rejected.' >&2
  exit 1
fi

echo 'PASS: SQL provider readiness rejects temporary init daemons.'
