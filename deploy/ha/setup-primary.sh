#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_root
ha_need_env
node="${1:-A}"
local_ip="${2:-}"
peer_ip="${3:-}"
a_public_ip="${4:-$(ha_read_env HA_NODE_A_PUBLIC_IP)}"
b_public_ip="${5:-$(ha_read_env HA_NODE_B_PUBLIC_IP)}"
[ "$node" = A ] || [ "$node" = B ] || ha_die "node must be A or B"
[ -n "$local_ip" ] && [ -n "$peer_ip" ] || ha_die "usage: sudo ./ha/setup-primary.sh A 10.80.0.1 10.80.0.2 [A_PUBLIC_IP] [B_PUBLIC_IP]"
ha_require_local_ip "$local_ip"

if [ -n "$TASKFORGE_BACKUP_HELPER" ] && [ "${HA_SKIP_BACKUP:-0}" != 1 ]; then
  echo "Creating a pre-HA metadata/database backup..."
  "$TASKFORGE_BACKUP_HELPER" --metadata
  "$TASKFORGE_BACKUP_HELPER" --database
fi

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
ha_set_env TASKFORGE_NODE_ROLE primary

"$TASKFORGE_SECRET_HELPER" --yes
ha_check
ha_compose up -d postgres rabbitmq redis minio
ha_agent configure-primary
ha_compose up -d --remove-orphans
"$HA_DIR/install-service.sh"
sleep 2
ha_agent reconcile

echo
echo "Primary HA node $node is configured."
echo "Local health: http://$local_ip:$(ha_read_env HA_AGENT_PORT)/ha/traffic-ready"
echo "Next: create B.env with ./ha/make-peer-env.sh and initialize B as standby."
