#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
cluster_need_env
cluster_need_config
if [ ! -f "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-complete" ] && [ ! -f "$TASKFORGE_CLUSTER_RUNTIME/join-complete" ]; then
  cluster_die "this node has not been migrated/joined; use migrate-primary.sh or join-node.sh"
fi
cluster_render
cluster_enable_marker
"$CLUSTER_DIR/ops/quorum/install-service.sh"
echo "TaskForge cluster runtime enabled on node $(cluster_node_id)."
