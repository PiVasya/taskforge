#!/usr/bin/env bash
set -Eeuo pipefail

CLUSTER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ -f "$CLUSTER_DIR/../compose.sh" ]; then
  TASKFORGE_ROOT="$(cd "$CLUSTER_DIR/.." && pwd)"
  TASKFORGE_COMPOSE="$TASKFORGE_ROOT/compose.sh"
  TASKFORGE_ENV_FILE="${TASKFORGE_ENV_FILE:-$TASKFORGE_ROOT/.env}"
  TASKFORGE_CHECK="$TASKFORGE_ROOT/check.sh"
  TASKFORGE_BACKUP="$TASKFORGE_ROOT/scripts/maintenance/backup.sh"
else
  TASKFORGE_ROOT="$(cd "$CLUSTER_DIR/../.." && pwd)"
  TASKFORGE_COMPOSE="$TASKFORGE_ROOT/deploy/prod/compose.sh"
  TASKFORGE_ENV_FILE="${TASKFORGE_ENV_FILE:-$TASKFORGE_ROOT/deploy/prod/.env}"
  TASKFORGE_CHECK="$TASKFORGE_ROOT/scripts/prod/check-prod-config.sh"
  TASKFORGE_BACKUP=""
fi
TASKFORGE_CLUSTER_CONFIG="${TASKFORGE_CLUSTER_CONFIG:-$CLUSTER_DIR/cluster.json}"
TASKFORGE_CLUSTER_RUNTIME="$TASKFORGE_ROOT/.runtime/cluster"
TASKFORGE_READINESS_RUNTIME="$TASKFORGE_CLUSTER_RUNTIME/readiness"
export TASKFORGE_ROOT TASKFORGE_ENV_FILE TASKFORGE_CLUSTER_CONFIG TASKFORGE_CLUSTER_RUNTIME TASKFORGE_READINESS_RUNTIME

# Replica-mode C can expose PostgreSQL/MinIO through TaskForge-managed socat
# units. Keep those endpoints alive during quorum preparation and retire them
# only at the exact standalone -> Patroni handoff.
# shellcheck source=proxy.sh
source "$CLUSTER_DIR/lib/proxy.sh"

cluster_die(){ echo "error: $*" >&2; exit 2; }
cluster_warn(){ echo "warning: $*" >&2; }
cluster_info(){ echo "[taskforge-cluster] $*"; }
cluster_need_root(){ [ "$(id -u)" -eq 0 ] || cluster_die "run this command with sudo/root"; }
cluster_need_env(){ [ -f "$TASKFORGE_ENV_FILE" ] || cluster_die "missing $TASKFORGE_ENV_FILE"; }
cluster_need_config(){ [ -f "$TASKFORGE_CLUSTER_CONFIG" ] || cluster_die "missing $TASKFORGE_CLUSTER_CONFIG; copy cluster.example.json to cluster.json and fill it"; }
cluster_need_command(){ command -v "$1" >/dev/null 2>&1 || cluster_die "$1 is required"; }
cluster_project_owner(){ stat -c '%u:%g' "$TASKFORGE_ROOT"; }
cluster_fix_runtime_owner(){
  if [ "$(id -u)" -eq 0 ] && [ -d "$TASKFORGE_CLUSTER_RUNTIME" ]; then
    chown -R "$(cluster_project_owner)" "$TASKFORGE_CLUSTER_RUNTIME"
  fi
}
clusterctl(){ python3 "$CLUSTER_DIR/clusterctl.py" --config "$TASKFORGE_CLUSTER_CONFIG" --env-file "$TASKFORGE_ENV_FILE" "$@"; }
cluster_compose(){ "$TASKFORGE_COMPOSE" "$@"; }
cluster_compose_force(){ TASKFORGE_CLUSTER_FORCE=1 "$TASKFORGE_COMPOSE" "$@"; }
cluster_node_id(){ [ -f "$TASKFORGE_CLUSTER_RUNTIME/node-id" ] || cluster_die "local node id is not set; run bash ./cluster.sh quorum-set-node NODE_ID"; tr -d '\r\n' < "$TASKFORGE_CLUSTER_RUNTIME/node-id"; }
cluster_read_env(){
  python3 - "$TASKFORGE_ENV_FILE" "$1" <<'PY'
from pathlib import Path
import sys
value=''
for raw in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if not raw or raw.lstrip().startswith('#') or '=' not in raw:
        continue
    key,val=raw.split('=',1)
    if key.strip()==sys.argv[2]:
        value=val.strip().strip('"').strip("'")
print(value)
PY
}
cluster_project_name(){
  local value
  value="$(cluster_read_env COMPOSE_PROJECT_NAME)"
  printf '%s\n' "${value:-taskforge-prod}"
}
cluster_derived_secret(){
  local purpose="$1" length="${2:-64}" key
  key="$(cluster_read_env TASKFORGE_INTERNAL_KEY)"
  [ "${#key}" -ge 40 ] || cluster_die "TASKFORGE_INTERNAL_KEY must contain at least 40 characters"
  python3 - "$key" "$purpose" "$length" <<'PY_SECRET'
import hashlib,hmac,sys
key,purpose,length=sys.argv[1],sys.argv[2],int(sys.argv[3])
print(hmac.new(key.encode('utf-8'), purpose.encode('utf-8'), hashlib.sha512).hexdigest()[:length])
PY_SECRET
}
cluster_runtime_prepare(){
  mkdir -p "$TASKFORGE_CLUSTER_RUNTIME" "$TASKFORGE_CLUSTER_RUNTIME/tls" \
    "$TASKFORGE_CLUSTER_RUNTIME/shared/content-data-protection" \
    "$TASKFORGE_CLUSTER_RUNTIME/shared/quiz-data-protection" \
    "$TASKFORGE_CLUSTER_RUNTIME/bin" \
    "$TASKFORGE_READINESS_RUNTIME"
  if [ "$(id -u)" -eq 0 ]; then
    chown "$(cluster_project_owner)" \
      "$TASKFORGE_CLUSTER_RUNTIME" "$TASKFORGE_CLUSTER_RUNTIME/tls" \
      "$TASKFORGE_CLUSTER_RUNTIME/shared" \
      "$TASKFORGE_CLUSTER_RUNTIME/shared/content-data-protection" \
      "$TASKFORGE_CLUSTER_RUNTIME/shared/quiz-data-protection" \
      "$TASKFORGE_CLUSTER_RUNTIME/bin" "$TASKFORGE_READINESS_RUNTIME"
  fi
  chmod 700 "$TASKFORGE_CLUSTER_RUNTIME" "$TASKFORGE_CLUSTER_RUNTIME/tls" "$TASKFORGE_CLUSTER_RUNTIME/bin" 2>/dev/null || true
  chmod 755 "$TASKFORGE_READINESS_RUNTIME" 2>/dev/null || true
}
cluster_render(){
  cluster_runtime_prepare
  clusterctl render
  cluster_fix_runtime_owner
}
cluster_is_enabled(){ [ -f "$TASKFORGE_CLUSTER_RUNTIME/enabled" ]; }
cluster_enable_marker(){ cluster_runtime_prepare; printf '%s\n' "enabled $(date -u +%FT%TZ)" > "$TASKFORGE_CLUSTER_RUNTIME/enabled"; chmod 600 "$TASKFORGE_CLUSTER_RUNTIME/enabled"; }
cluster_disable_marker(){ rm -f "$TASKFORGE_CLUSTER_RUNTIME/enabled" "$TASKFORGE_READINESS_RUNTIME/traffic-ready"; }
cluster_has_wg_ip(){
  local ip="$1"
  cluster_need_command ip
  ip -o addr show | awk '{print $4}' | cut -d/ -f1 | grep -Fxq "$ip" || cluster_die "WireGuard address $ip is not present; configure wg-taskforge first"
}
cluster_retire_replica_data_proxies(){
  local wg_ip pg_port minio_port unit
  wg_ip="$(cluster_local_field 'd["wireguard"]["ip"]')"
  pg_port="$(cluster_local_field 'd["postgres"].get("cluster_port",5432)')"
  minio_port="$(cluster_local_field 'd["minio"].get("cluster_port",9000)')"
  unit="$(proxy_unit_name postgres)"
  if proxy_unit_known "$unit"; then
    cluster_info "retiring replica PostgreSQL WG proxy at $wg_ip:$pg_port for Patroni handoff"
    proxy_remove_managed_unit "$unit" "$wg_ip" "$pg_port" || cluster_die "cannot retire replica PostgreSQL WG proxy"
  fi
  unit="$(proxy_unit_name minio)"
  if proxy_unit_known "$unit"; then
    cluster_info "retiring replica MinIO WG proxy at $wg_ip:$minio_port for direct quorum-mode bind"
    proxy_remove_managed_unit "$unit" "$wg_ip" "$minio_port" || cluster_die "cannot retire replica MinIO WG proxy"
  fi
}
cluster_local_field(){
  local expression="$1"
  clusterctl node-info | python3 -c "import json,sys; d=json.load(sys.stdin); print($expression)"
}
cluster_volume_name(){
  local logical="$1" project found
  project="$(cluster_project_name)"
  found="$(docker volume ls -q \
    --filter "label=com.docker.compose.project=$project" \
    --filter "label=com.docker.compose.volume=$logical" | head -n1 || true)"
  if [ -n "$found" ]; then
    printf '%s\n' "$found"
  else
    printf '%s_%s\n' "$project" "$logical"
  fi
}
cluster_node_json_field(){
  local node_id="$1" expression="$2"
  python3 - "$TASKFORGE_CLUSTER_CONFIG" "$node_id" "$expression" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
node=next((n for n in cfg['nodes'] if n['id']==sys.argv[2]),None)
if node is None:
    raise SystemExit(f'node not found: {sys.argv[2]}')
# Only trusted expressions from bundled scripts reach this helper.
print(eval(sys.argv[3], {'__builtins__': {}}, {'n': node, 'cfg': cfg}))
PY
}
cluster_wait_http(){
  local url="$1" expected="${2:-200}" timeout="${3:-120}" auth_user="${4:-}" auth_password="${5:-}"
  python3 - "$url" "$expected" "$timeout" "$auth_user" "$auth_password" <<'PY'
import base64,sys,time,urllib.error,urllib.request
url,expected,timeout,user,password=sys.argv[1],int(sys.argv[2]),int(sys.argv[3]),sys.argv[4],sys.argv[5]
deadline=time.monotonic()+timeout
while time.monotonic()<deadline:
    req=urllib.request.Request(url)
    if user:
        token=base64.b64encode(f'{user}:{password}'.encode()).decode()
        req.add_header('Authorization','Basic '+token)
    try:
        with urllib.request.urlopen(req,timeout=3) as response:
            if response.status==expected:
                raise SystemExit(0)
    except urllib.error.HTTPError as exc:
        if exc.code==expected:
            raise SystemExit(0)
    except Exception:
        pass
    time.sleep(2)
raise SystemExit(f'timed out waiting for {url} -> HTTP {expected}')
PY
}
