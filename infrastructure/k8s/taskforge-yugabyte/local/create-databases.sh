#!/usr/bin/env bash
set -Eeuo pipefail

# Fail closed if this kubeconfig points outside the dedicated LOCAL K3s.
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/guard-local-context.sh"
[[ "${TASKFORGE_LOCAL_K3S_ACK:-}" == 'local-development-only' ]] || { echo 'local only' >&2; exit 2; }
THIS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$THIS/verify-db.sh"
kubectl -n "taskforge-k3s-lab-db" exec -i taskforge-k3s-lab-yb-yb-tserver-0 -- \
  ysqlsh -h 127.0.0.1 -p 5433 -U yugabyte -d yugabyte -v ON_ERROR_STOP=1 \
  < "$THIS/../db/create-k3s-lab-databases.sql"
echo 'Created or retained TaskForge logical databases inside YugabyteDB.'
