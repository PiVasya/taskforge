#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_env
cluster_need_config
clusterctl validate
cluster_render >/dev/null
printf '%s\n' '=== LOCAL NODE ==='
clusterctl node-info
printf '\n%s\n' '=== AGENT ==='
if [ -f "$TASKFORGE_CLUSTER_RUNTIME/agent-state.json" ]; then
  cat "$TASKFORGE_CLUSTER_RUNTIME/agent-state.json"
else
  echo '{"status":"not-started"}'
fi
printf '\n%s\n' '=== PATRONI NODES ==='
clusterctl status || true
printf '\n%s\n' '=== CONTAINERS ==='
cluster_compose ps || true
printf '\n%s\n' '=== WIREGUARD ==='
wg show wg-taskforge 2>/dev/null || true
