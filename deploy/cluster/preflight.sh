#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_env
cluster_need_config
command -v python3 >/dev/null 2>&1 || cluster_die "python3 is required"
command -v docker >/dev/null 2>&1 || cluster_die "docker is required"
command -v wg >/dev/null 2>&1 || cluster_die "wireguard-tools is required"
clusterctl validate
cluster_render >/dev/null
node_json="$(clusterctl node-info)"
wg_ip="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["wireguard"]["ip"])')"
tls_mode="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["web"].get("tls_mode","origin-ca"))')"
cluster_has_wg_ip "$wg_ip"
if [ "$tls_mode" = origin-ca ]; then
  [ -s "$TASKFORGE_CLUSTER_RUNTIME/tls/fullchain.pem" ] || cluster_die "missing .runtime/cluster/tls/fullchain.pem"
  [ -s "$TASKFORGE_CLUSTER_RUNTIME/tls/privkey.pem" ] || cluster_die "missing .runtime/cluster/tls/privkey.pem"
  chmod 600 "$TASKFORGE_CLUSTER_RUNTIME/tls/privkey.pem" 2>/dev/null || true
fi
cluster_compose_force config >/dev/null
if [ -x "$TASKFORGE_CHECK" ]; then "$TASKFORGE_CHECK"; fi
python3 - "$TASKFORGE_CLUSTER_CONFIG" "$wg_ip" <<'PY'
import json, socket, sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
local=sys.argv[2]
failed=[]
for node in cfg['nodes']:
    ip=node['wireguard']['ip']
    if ip==local: continue
    for label,port in (
        ('patroni',node['postgres']['patroni_rest_port']),
        ('postgres',node['postgres']['host_port']),
        ('minio',node['minio']['api_port']),
    ):
        s=socket.socket(); s.settimeout(1.5)
        try: s.connect((ip,int(port)))
        except OSError: failed.append(f'{node["id"]}:{label} {ip}:{port}')
        finally: s.close()
if failed:
    print('warning: peer services not reachable yet: '+', '.join(failed),file=sys.stderr)
PY
echo "cluster preflight ok"
