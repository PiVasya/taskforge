#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
if systemctl cat taskforge-cluster.service >/dev/null 2>&1; then
  systemctl disable --now taskforge-cluster.service >/dev/null
  for _ in $(seq 1 50); do
    systemctl is-active --quiet taskforge-cluster.service || break
    sleep 0.2
  done
  systemctl is-active --quiet taskforge-cluster.service && cluster_die 'taskforge-cluster.service did not stop'
fi
cluster_compose_force stop >/dev/null
cluster_disable_marker
echo "Cluster runtime disabled. PostgreSQL cluster volumes were preserved."
