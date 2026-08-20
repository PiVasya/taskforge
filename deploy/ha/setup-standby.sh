#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_root
ha_need_env
ASSUME=0
if [ "${1:-}" = "--yes" ]; then ASSUME=1; shift; fi
node="${1:-B}"
local_ip="${2:-}"
peer_ip="${3:-}"
a_public_ip="${4:-$(ha_read_env HA_NODE_A_PUBLIC_IP)}"
b_public_ip="${5:-$(ha_read_env HA_NODE_B_PUBLIC_IP)}"
[ "$node" = A ] || [ "$node" = B ] || ha_die "node must be A or B"
[ -n "$local_ip" ] && [ -n "$peer_ip" ] || ha_die "usage: sudo ./ha/setup-standby.sh --yes B 10.80.0.2 10.80.0.1 [A_PUBLIC_IP] [B_PUBLIC_IP]"
[ "$ASSUME" -eq 1 ] || ha_die "this replaces the local PostgreSQL data volume; pass --yes"
ha_require_local_ip "$local_ip"
ping -c 1 -W 3 "$peer_ip" >/dev/null 2>&1 || ha_die "peer $peer_ip is not reachable over WireGuard"

ha_set_env HA_ENABLED true
ha_set_env HA_NODE_ID "$node"
ha_set_env HA_PREFERRED_NODE A
# Keep unattended promotion disabled until replication, storage sync, fencing and Cloudflare are verified.
ha_set_env HA_AUTO_FAILOVER false
ha_set_env HA_WG_IP "$local_ip"
ha_set_env HA_PEER_WG_IP "$peer_ip"
[ -n "$a_public_ip" ] && ha_set_env HA_NODE_A_PUBLIC_IP "$a_public_ip"
[ -n "$b_public_ip" ] && ha_set_env HA_NODE_B_PUBLIC_IP "$b_public_ip"
ha_set_env POSTGRES_BIND "$local_ip"
ha_set_env MINIO_BIND "$local_ip"
ha_set_env POSTGRES_RESTART_POLICY no
ha_set_env TASKFORGE_NODE_ROLE standby

"$TASKFORGE_SECRET_HELPER" --yes
ha_check
ha_agent status >/dev/null
ha_compose stop >/dev/null 2>&1 || true
ha_compose up -d rabbitmq redis minio
ha_agent rejoin --yes
"$HA_DIR/install-service.sh"
sleep 2
ha_agent reconcile

echo
echo "Standby HA node $node now follows $peer_ip."
echo "Production traffic remains disabled until promotion."
