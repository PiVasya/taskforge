#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_root
cluster_need_env
cluster_need_config
node="${1:-}"
[ -n "$node" ] || cluster_die "usage: sudo ./cluster/set-node.sh NODE_ID"
clusterctl validate
clusterctl set-node "$node"
cluster_render
printf '\nNode %s is prepared locally. Cluster mode is NOT enabled yet.\n' "$node"
