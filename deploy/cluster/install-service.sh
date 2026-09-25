#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_root
cluster_need_env
cluster_need_config
cluster_render
unit=/etc/systemd/system/taskforge-cluster.service
cat > "$unit" <<UNIT
[Unit]
Description=TaskForge Patroni application-role controller
Requires=docker.service
After=docker.service network-online.target wg-quick@wg-taskforge.service
Wants=network-online.target wg-quick@wg-taskforge.service

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
systemctl daemon-reload
systemctl enable taskforge-cluster.service
systemctl restart taskforge-cluster.service
systemctl --no-pager --full status taskforge-cluster.service || true
