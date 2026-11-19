#!/usr/bin/env bash
set -Eeuo pipefail
if [[ "${TASKFORGE_YUGABYTE_LAB_ACK:-}" != 'yes-lab-only' ]]; then
  echo 'Lab only. Refusing to create databases on an unspecified cluster.' >&2
  exit 2
fi
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$HERE/verify-lab.sh"
kubectl -n "${YB_NAMESPACE:-taskforge-db}" exec -i taskforge-yb-yb-tserver-0 -- \
  ysqlsh -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte \
  < "$HERE/create-databases.sql"
echo 'Lab logical databases created (existing databases preserved).'
