#!/usr/bin/env bash
set -Eeuo pipefail

# Fail closed if this kubeconfig points outside the dedicated LOCAL K3s.
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/guard-local-context.sh"
# Actual YSQL write + independent read, safely isolated from TaskForge business tables.
[[ "${TASKFORGE_LOCAL_K3S_ACK:-}" == local-development-only ]] || { echo 'local only' >&2; exit 2; }
NS="taskforge-k3s-lab-db"
POD=taskforge-k3s-lab-yb-yb-tserver-0
kubectl -n "$NS" exec "$POD" -- ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte -v ON_ERROR_STOP=1 -qAtc \
  'CREATE TABLE IF NOT EXISTS taskforge_k3s_lab_write_probe (id INT PRIMARY KEY, checked_at TIMESTAMPTZ NOT NULL);'
kubectl -n "$NS" exec "$POD" -- ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte -v ON_ERROR_STOP=1 -qAtc \
  'INSERT INTO taskforge_k3s_lab_write_probe (id, checked_at) VALUES (1, now()) ON CONFLICT (id) DO UPDATE SET checked_at=excluded.checked_at;'
count="$(kubectl -n "$NS" exec "$POD" -- ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte -v ON_ERROR_STOP=1 -qAtc 'SELECT count(*) FROM taskforge_k3s_lab_write_probe WHERE id=1;')"
[[ "$count" == 1 ]] || { echo "ERROR: independent YSQL read returned $count" >&2; exit 1; }
echo 'PASS: real INSERT/UPSERT committed and read back in a second YSQL session on K3s YugabyteDB RF1.'
