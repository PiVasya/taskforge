#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"

cluster_need_root
cluster_need_env
cluster_need_config
cluster_need_command python3
cluster_need_command wg
cluster_need_command wg-quick
clusterctl validate
cluster_render >/dev/null

"$CLUSTER_DIR/wireguard-apply.sh"

# Patroni keeps local tags/endpoints in memory. Ask it to reload the freshly
# rendered local configuration without restarting PostgreSQL or forcing a
# failover. A node that has not joined yet simply skips this step.
node_json="$(clusterctl node-info)"
wg_ip="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["wireguard"]["ip"])')"
patroni_port="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["postgres"]["patroni_rest_port"])')"
if python3 - "$TASKFORGE_ENV_FILE" "$wg_ip" "$patroni_port" <<'PY_RELOAD'
import base64, hashlib, hmac, ipaddress, sys, urllib.request
from pathlib import Path

env_path, host, port = Path(sys.argv[1]), sys.argv[2], int(sys.argv[3])
values = {}
for raw in env_path.read_text(encoding="utf-8-sig").splitlines():
    if not raw or raw.lstrip().startswith("#") or "=" not in raw:
        continue
    key, value = raw.split("=", 1)
    values[key.strip()] = value.strip().strip('"').strip("'")
internal = values.get("TASKFORGE_INTERNAL_KEY", "")
if len(internal) < 40:
    raise SystemExit(1)
password = hmac.new(internal.encode(), b"taskforge-patroni-rest-v2", hashlib.sha512).hexdigest()[:64]
token = base64.b64encode(f"taskforge_patroni:{password}".encode()).decode()
try:
    parsed = ipaddress.ip_address(host)
    url_host = f"[{parsed}]" if parsed.version == 6 else str(parsed)
except ValueError:
    url_host = host
request = urllib.request.Request(
    f"http://{url_host}:{port}/reload",
    data=b"{}",
    method="POST",
    headers={"Authorization": "Basic " + token, "Content-Type": "application/json"},
)
try:
    with urllib.request.urlopen(request, timeout=5) as response:
        if response.status not in {200, 202}:
            raise SystemExit(1)
except Exception:
    raise SystemExit(1)
PY_RELOAD
then
  cluster_info "Patroni reloaded the updated local topology/configuration"
else
  cluster_warn "local Patroni is not running yet or rejected reload; it will read the topology on its next start"
fi

if command -v systemctl >/dev/null 2>&1 && systemctl cat taskforge-cluster.service >/dev/null 2>&1; then
  if systemctl is-active --quiet taskforge-cluster.service; then
    cluster_info "restarting TaskForge cluster controller so it reloads the new topology"
    systemctl restart taskforge-cluster.service
  else
    cluster_warn "taskforge-cluster.service exists but is not active; topology is rendered and will be loaded on its next start"
  fi
fi

cluster_info "topology applied on node $(cluster_node_id)"
"$CLUSTER_DIR/wireguard-status.sh" || true
