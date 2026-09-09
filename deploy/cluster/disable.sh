#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_root
systemctl disable --now taskforge-cluster.service 2>/dev/null || true
cluster_compose_force stop >/dev/null 2>&1 || true
cluster_disable_marker
echo "Cluster runtime disabled. PostgreSQL cluster volumes were preserved."
