#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_root
ha_need_env
command -v systemctl >/dev/null 2>&1 || ha_die "systemd/systemctl is required"
command -v python3 >/dev/null 2>&1 || ha_die "python3 is required"
command -v docker >/dev/null 2>&1 || ha_die "docker is required"
mkdir -p "$TASKFORGE_ROOT/.runtime/ha"
chmod 700 "$TASKFORGE_ROOT/.runtime/ha"

cat > /etc/systemd/system/taskforge-ha.service <<EOF2
[Unit]
Description=TaskForge two-node HA controller
Wants=network-online.target docker.service wg-quick@wg-taskforge.service
After=network-online.target docker.service wg-quick@wg-taskforge.service

[Service]
Type=simple
WorkingDirectory=$TASKFORGE_ROOT
Environment=TASKFORGE_ENV_FILE=$TASKFORGE_ENV_FILE
ExecStart=/usr/bin/python3 $HA_DIR/agent.py --env-file $TASKFORGE_ENV_FILE daemon
ExecStopPost=/usr/bin/rm -f $TASKFORGE_ROOT/.runtime/ha/traffic-ready
Restart=always
RestartSec=5
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF2

cat > /etc/systemd/system/taskforge-ha-state-sync.service <<EOF2
[Unit]
Description=TaskForge HA copy TLS/DataProtection state to standby
After=taskforge-ha.service network-online.target

[Service]
Type=oneshot
WorkingDirectory=$TASKFORGE_ROOT
Environment=TASKFORGE_ENV_FILE=$TASKFORGE_ENV_FILE
ExecStart=$HA_DIR/sync-shared-volumes.sh
EOF2
cat > /etc/systemd/system/taskforge-ha-state-sync.timer <<'EOF2'
[Unit]
Description=Periodic TaskForge HA shared-state sync

[Timer]
OnBootSec=5min
OnUnitActiveSec=10min
RandomizedDelaySec=30s
Persistent=true

[Install]
WantedBy=timers.target
EOF2

cat > /etc/systemd/system/taskforge-ha-prefetch.service <<EOF2
[Unit]
Description=TaskForge HA standby image prefetch
After=docker.service network-online.target

[Service]
Type=oneshot
WorkingDirectory=$TASKFORGE_ROOT
Environment=TASKFORGE_ENV_FILE=$TASKFORGE_ENV_FILE
ExecStart=$HA_DIR/prefetch-images.sh
EOF2
cat > /etc/systemd/system/taskforge-ha-prefetch.timer <<'EOF2'
[Unit]
Description=Periodic TaskForge HA standby image prefetch

[Timer]
OnBootSec=3min
OnUnitActiveSec=10min
RandomizedDelaySec=45s
Persistent=true

[Install]
WantedBy=timers.target
EOF2

systemctl daemon-reload
systemctl enable taskforge-ha.service taskforge-ha-state-sync.timer taskforge-ha-prefetch.timer
systemctl restart taskforge-ha.service
systemctl restart taskforge-ha-state-sync.timer taskforge-ha-prefetch.timer
sleep 2
systemctl --no-pager --full status taskforge-ha.service || true
