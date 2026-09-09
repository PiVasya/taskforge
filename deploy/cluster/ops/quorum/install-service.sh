#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
cluster_need_env
cluster_need_config
cluster_render
unit=/etc/systemd/system/taskforge-cluster.service
tmp="$(mktemp /etc/systemd/system/.taskforge-cluster.service.XXXXXX)"
trap 'rm -f "$tmp"' EXIT
chmod 0644 "$tmp"
cat > "$tmp" <<UNIT
[Unit]
Description=TaskForge Node Agent and HA application controller
After=docker.service network-online.target wg-quick@wg-taskforge.service
Wants=docker.service network-online.target wg-quick@wg-taskforge.service

[Service]
Type=simple
WorkingDirectory=$TASKFORGE_ROOT
Environment="TASKFORGE_ENV_FILE=$TASKFORGE_ENV_FILE"
Environment="TASKFORGE_CLUSTER_CONFIG=$TASKFORGE_CLUSTER_CONFIG"
ExecStartPre=/usr/bin/python3 "$CLUSTER_DIR/clusterctl.py" --config "$TASKFORGE_CLUSTER_CONFIG" --env-file "$TASKFORGE_ENV_FILE" render
ExecStart=/usr/bin/python3 "$CLUSTER_DIR/agent.py" --config "$TASKFORGE_CLUSTER_CONFIG" --env-file "$TASKFORGE_ENV_FILE"
ExecStopPost=/usr/bin/rm -f "$TASKFORGE_READINESS_RUNTIME/traffic-ready"
Restart=always
RestartSec=5
TimeoutStartSec=0
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
UNIT
mv -f "$tmp" "$unit"
trap - EXIT
systemctl daemon-reload
systemctl enable taskforge-cluster.service
systemctl restart taskforge-cluster.service
# Do not treat `restart` return as application readiness. Wait for systemd to
# report active before showing status to the operator.
for _ in $(seq 1 50); do
  systemctl is-active --quiet taskforge-cluster.service && break
  sleep 0.2
done
systemctl is-active --quiet taskforge-cluster.service || {
  systemctl --no-pager --full status taskforge-cluster.service || true
  exit 1
}
systemctl --no-pager --full status taskforge-cluster.service || true
