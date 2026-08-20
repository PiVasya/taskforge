#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_env
port="$(ha_read_env HA_AGENT_PORT)"; port="${port:-9187}"
echo '=== local agent ==='
curl -fsS "http://127.0.0.1:$port/ha/live"; echo
curl -i -sS "http://127.0.0.1:$port/ha/traffic-ready" | sed -n '1,12p'
echo '=== cluster status ==='
ha_agent status --peer
echo '=== MinIO replication ==='
"$HA_DIR/minio-wait-replication.sh" --check-only
