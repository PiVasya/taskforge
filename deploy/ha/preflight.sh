#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_env
local_ip="$(ha_read_env HA_WG_IP)"
peer_ip="$(ha_read_env HA_PEER_WG_IP)"
port="$(ha_read_env HA_AGENT_PORT)"; port="${port:-9187}"
ha_require_local_ip "$local_ip"
echo "[1/7] configuration"
ha_check >/dev/null
echo "  OK"
echo "[2/7] WireGuard peer $peer_ip"
ping -c 2 -W 3 "$peer_ip" >/dev/null && echo "  OK" || ha_die "WireGuard peer is unreachable"
echo "[3/7] peer HA agent"
python3 - "$peer_ip" "$port" <<'PY'
import sys,urllib.request
url=f'http://{sys.argv[1]}:{sys.argv[2]}/ha/live'
with urllib.request.urlopen(url, timeout=5) as r:
    assert r.status == 200
print('  OK')
PY
echo "[4/7] PostgreSQL role/replication"
ha_agent status --peer
echo "[5/7] MinIO peer port"
python3 - "$peer_ip" "$(ha_read_env MINIO_PORT)" <<'PY'
import socket,sys
port=int(sys.argv[2] or '9000')
with socket.create_connection((sys.argv[1],port),5): pass
print('  OK')
PY
echo "[6/7] MinIO bucket replication rule"
"$HA_DIR/minio-wait-replication.sh" --check-only
echo "[7/7] systemd"
if command -v systemctl >/dev/null 2>&1; then
  systemctl is-enabled taskforge-ha.service >/dev/null 2>&1 && echo "  taskforge-ha enabled" || echo "  warning: taskforge-ha not enabled"
fi
echo "HA preflight complete."
