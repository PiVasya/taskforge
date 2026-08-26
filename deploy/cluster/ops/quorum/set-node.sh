#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
cluster_need_env
cluster_need_config
node="${1:-}"
[ -n "$node" ] || cluster_die "usage: sudo ./cluster/ops/quorum/set-node.sh NODE_ID"
clusterctl validate
clusterctl set-node "$node"
cluster_render
printf '\nNode %s is prepared locally. Cluster mode is NOT enabled yet.\n' "$node"
