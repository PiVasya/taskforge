#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_env
cluster_need_config
cluster_render
voter="$(clusterctl node-info | python3 -c 'import json,sys; print(str(json.load(sys.stdin).get("dcs_voter",False)).lower())')"
if [ "$voter" != true ]; then
  echo "This node is not an etcd voter; nothing to start."
  exit 0
fi
cluster_compose_force up -d etcd
for _ in $(seq 1 30); do
  if cluster_compose_force ps --status running etcd 2>/dev/null | grep -q etcd; then
    echo "Local etcd container is running on node $(cluster_node_id)."
    echo "Start etcd on the other voter nodes, then run: sudo ./cluster/ops/quorum/wait-dcs.sh"
    if [ "${1:-}" = "--wait-quorum" ]; then
      "$CLUSTER_DIR/ops/quorum/wait-dcs.sh" "${2:-180}"
    fi
    exit 0
  fi
  sleep 2
done
cluster_compose_force logs --tail 120 etcd || true
cluster_die "local etcd container did not start"
