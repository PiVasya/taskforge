#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_config
clusterctl validate
if command -v wg >/dev/null 2>&1; then
  wg show wg-taskforge || true
else
  echo "wireguard-tools not installed"
fi
echo
clusterctl node-info
