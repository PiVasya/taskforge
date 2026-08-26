#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
cluster_need_env
cluster_need_config
[ -s /etc/wireguard/taskforge-private.key ] || cluster_die "run ./cluster/ops/wireguard/generate.sh first"
clusterctl validate
cluster_render
clusterctl render-wireguard \
  --private-key-file /etc/wireguard/taskforge-private.key \
  --output /etc/wireguard/wg-taskforge.conf >/dev/null
chmod 600 /etc/wireguard/wg-taskforge.conf
if command -v systemctl >/dev/null 2>&1; then
  systemctl enable wg-quick@wg-taskforge
  systemctl restart wg-quick@wg-taskforge
else
  wg-quick down wg-taskforge 2>/dev/null || true
  wg-quick up wg-taskforge
fi
wg show wg-taskforge
