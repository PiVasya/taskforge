#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
[ "${1:-}" = "--yes" ] || cluster_die "usage: sudo ./cluster/ops/quorum/rollback-bootstrap.sh --yes"
node="$(cluster_node_id)"
preferred="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["preferred_primary"])' "$TASKFORGE_CLUSTER_CONFIG")"
[ "$node" = "$preferred" ] || cluster_die "bootstrap rollback is only for preferred node $preferred"
cat >&2 <<'WARN'
WARNING: this returns to the preserved standalone PostgreSQL volume.
Any writes accepted by the new Patroni cluster after migration will not exist in
that old volume. Use this only during the initial migration failure, before real
users resume work.
WARN
if systemctl cat taskforge-cluster.service >/dev/null 2>&1; then
  systemctl disable --now taskforge-cluster.service >/dev/null || cluster_die 'failed to stop taskforge-cluster.service'
fi
cluster_compose_force down --remove-orphans
cluster_disable_marker
cluster_compose up -d
printf '%s\n' "rolled-back $(date -u +%FT%TZ)" > "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-rolled-back"
chmod 600 "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-rolled-back"
cluster_info "standalone TaskForge stack started from preserved original volumes"
cluster_warn "before retrying the migration, use migrate-primary.sh --yes --reset-cluster-data"
