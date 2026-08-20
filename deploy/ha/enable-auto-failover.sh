#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_root
ha_need_env
[ "$(ha_read_env HA_ENABLED)" = true ] || ha_die "HA_ENABLED is not true"
fence="$(ha_read_env HA_FENCE_SCRIPT)"
unsafe="$(ha_read_env HA_ALLOW_UNFENCED_FAILOVER)"
if [ -z "$fence" ] && ! ha_bool "$unsafe"; then
  ha_die "configure HA_FENCE_SCRIPT first (or explicitly choose HA_ALLOW_UNFENCED_FAILOVER=true)"
fi
if [ -n "$fence" ]; then
  if [[ "$fence" != /* ]]; then fence="$TASKFORGE_ROOT/$fence"; fi
  [ -x "$fence" ] || ha_die "fencing script is not executable: $fence"
fi
recover="$(ha_read_env HA_RECOVER_SCRIPT)"
if [ -n "$recover" ]; then
  if [[ "$recover" != /* ]]; then recover="$TASKFORGE_ROOT/$recover"; fi
  [ -x "$recover" ] || ha_die "recovery script is not executable: $recover"
else
  echo "warning: HA_RECOVER_SCRIPT is empty; failover can be automatic, but a fenced peer must be powered on manually before auto-failback." >&2
fi
"$HA_DIR/preflight.sh"
ha_set_env HA_AUTO_FAILOVER true
ha_check
systemctl restart taskforge-ha.service
sleep 2
ha_agent status --peer
echo "Automatic failover enabled on node $(ha_read_env HA_NODE_ID)."
