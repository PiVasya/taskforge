#!/usr/bin/env bash
set -Eeuo pipefail

CLUSTER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "$CLUSTER_DIR/../compose.sh" ]; then
  ROOT="$(cd "$CLUSTER_DIR/.." && pwd)"
  COMPOSE="$ROOT/compose.sh"
  CHECK="$ROOT/check.sh"
  BACKUP="$ROOT/scripts/maintenance/backup.sh"
  ENV_FILE="${TASKFORGE_ENV_FILE:-$ROOT/.env}"
else
  ROOT="$(cd "$CLUSTER_DIR/../.." && pwd)"
  COMPOSE="$ROOT/deploy/prod/compose.sh"
  CHECK="$ROOT/scripts/prod/check-prod-config.sh"
  BACKUP=""
  ENV_FILE="${TASKFORGE_ENV_FILE:-$ROOT/deploy/prod/.env}"
fi
MANAGER="$CLUSTER_DIR/manager.py"
ENSURE_HOST="$CLUSTER_DIR/ops/host/ensure-host.sh"
MINIO_REPL_LIB="$CLUSTER_DIR/lib/minio-replication.sh"
PROXY_LIB="$CLUSTER_DIR/lib/proxy.sh"
FIREWALL_LIB="$CLUSTER_DIR/lib/firewall.sh"
[ -r "$MINIO_REPL_LIB" ] || { echo "error: missing $MINIO_REPL_LIB" >&2; exit 2; }
[ -r "$PROXY_LIB" ] || { echo "error: missing $PROXY_LIB" >&2; exit 2; }
[ -r "$FIREWALL_LIB" ] || { echo "error: missing $FIREWALL_LIB" >&2; exit 2; }
source "$MINIO_REPL_LIB"
source "$PROXY_LIB"
source "$FIREWALL_LIB"
INVENTORY="${TASKFORGE_INVENTORY:-$CLUSTER_DIR/inventory.json}"
RUNTIME="$ROOT/.runtime/cluster"
EASY_RUNTIME="$RUNTIME/easy"

info(){ echo "[taskforge] $*"; }
warn(){ echo "warning: $*" >&2; }
die(){ echo "error: $*" >&2; exit 2; }
need_root(){ [ "$(id -u)" -eq 0 ] || die 'run with sudo/root'; }
need(){ command -v "$1" >/dev/null 2>&1 || die "$1 is required; run bash ./cluster.sh bootstrap"; }
auto_host_deps(){ need_root; [ -x "$ENSURE_HOST" ] || die "missing $ENSURE_HOST"; "$ENSURE_HOST" --quiet; }
owner_user(){ if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != root ]; then echo "$SUDO_USER"; else stat -c '%U' "$ROOT"; fi; }
owner_group(){ stat -c '%G' "$ROOT"; }
owner_pair(){ echo "$(owner_user):$(owner_group)"; }
fix_owner(){ [ ! -e "$1" ] || chown -R "$(owner_pair)" "$1"; }
prepare_runtime(){ mkdir -p "$RUNTIME" "$EASY_RUNTIME" "$RUNTIME/bin"; chmod 700 "$RUNTIME" "$EASY_RUNTIME" "$RUNTIME/bin" 2>/dev/null || true; [ "$(id -u)" -ne 0 ] || fix_owner "$RUNTIME"; }
acquire_operation_lock(){
  prepare_runtime
  command -v flock >/dev/null 2>&1 || die 'flock is required; run bash ./cluster.sh bootstrap'
  exec 9>"$EASY_RUNTIME/operation.lock"
  flock -n 9 || die 'another TaskForge cluster mutation is already running on this node'
}
need_env(){ [ -s "$ENV_FILE" ] || die "missing $ENV_FILE; import secrets or copy the shared .env first"; }
prepare_inventory(){
  if [ ! -s "$INVENTORY" ]; then
    [ -s "$CLUSTER_DIR/inventory.example.json" ] || die 'missing inventory.example.json'
    cp "$CLUSTER_DIR/inventory.example.json" "$INVENTORY"
    chmod 600 "$INVENTORY"
    [ "$(id -u)" -ne 0 ] || fix_owner "$INVENTORY"
    info "created $INVENTORY from the bundled A/B topology"
  fi
  python3 "$MANAGER" --inventory "$INVENTORY" validate >/dev/null
}
mgr(){ python3 "$MANAGER" --inventory "$INVENTORY" "$@"; }
env_get(){ python3 - "$ENV_FILE" "$1" <<'PY'
from pathlib import Path
import sys
for raw in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if raw.lstrip().startswith('#') or '=' not in raw: continue
    k,v=raw.split('=',1)
    if k.strip()==sys.argv[2]:
        print(v.strip().strip('"').strip("'")); break
PY
}
project(){ local x; x="$(env_get COMPOSE_PROJECT_NAME)"; echo "${x:-taskforge-prod}"; }
container(){ echo "$(project)-$1-1"; }
pg_container(){ container postgres; }
minio_container(){ container minio; }
volume(){ echo "$(project)_$1"; }
compose(){ "$COMPOSE" "$@"; }
node_id(){ [ -s "$RUNTIME/node-id" ] || die 'node id is not set; run bash ./cluster.sh apply NODE_ID'; tr -d '\r\n' < "$RUNTIME/node-id"; }
primary_id(){ python3 - "$INVENTORY" <<'PY'
import json,sys
print(json.load(open(sys.argv[1],encoding='utf-8'))['preferred_primary'])
PY
}
current_primary_id(){
  if [ -f "$RUNTIME/enabled" ] && [ -s "$RUNTIME/agent-state.json" ]; then
    local current
    current="$(python3 - "$RUNTIME/agent-state.json" "$INVENTORY" <<'PY' 2>/dev/null || true
import json,sys
state=json.load(open(sys.argv[1],encoding='utf-8'))
inv=json.load(open(sys.argv[2],encoding='utf-8'))
leader=str(state.get('leader') or '')
known={str(n.get('id')) for n in inv.get('nodes',[])}
print(leader if leader in known else '')
PY
)"
    [ -z "$current" ] || { printf '%s\n' "$current"; return 0; }
  fi
  primary_id
}
node_field(){ local node="$1" expr="$2"; mgr node-info --node "$node" | python3 -c "import json,sys; d=json.load(sys.stdin); print($expr)"; }
wait_health(){
  local name="$1" timeout="${2:-180}" deadline status
  deadline=$((SECONDS+timeout))
  while [ "$SECONDS" -lt "$deadline" ]; do
    status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$name" 2>/dev/null || true)"
    [ "$status" = healthy ] && return 0
    [ "$status" = running ] && return 0
    sleep 2
  done
  docker logs --tail 120 "$name" 2>/dev/null || true
  die "$name did not become healthy within ${timeout}s"
}
role_set(){ prepare_runtime; printf '%s\n' "$1" > "$EASY_RUNTIME/role"; chmod 600 "$EASY_RUNTIME/role"; [ "$(id -u)" -ne 0 ] || fix_owner "$EASY_RUNTIME/role"; }
role_get(){ [ -s "$EASY_RUNTIME/role" ] && cat "$EASY_RUNTIME/role" || echo unknown; }

# Run SQL without embedding SQL string quoting into nested `sh -lc` literals.
# v31 used \047 escapes in several queries; on real A/B those queries failed
# silently under stderr redirection, producing an empty standby receiver/status.
pg_scalar(){
  local pg="$1" sql="$2"
  docker exec "$pg" sh -lc 'exec psql -X -U "$POSTGRES_USER" -d postgres -Atc "$1"' _ "$sql"
}
pg_fields(){
  local pg="$1" sql="$2"
  docker exec "$pg" sh -lc 'exec psql -X -U "$POSTGRES_USER" -d postgres -AtF "|" -c "$1"' _ "$sql"
}
pg_actual_role(){
  local pg="${1:-$(pg_container)}" recovery
  docker inspect "$pg" >/dev/null 2>&1 || { echo unknown; return 0; }
  recovery="$(pg_scalar "$pg" 'select pg_is_in_recovery()' 2>/dev/null || true)"
  case "$recovery" in
    f) echo primary;;
    t) echo standby;;
    *) echo unknown;;
  esac
}
role_effective(){
  local marker actual
  marker="$(role_get)"
  actual="$(pg_actual_role 2>/dev/null || echo unknown)"
  case "$actual" in primary|standby) echo "$actual";; *) echo "$marker";; esac
}
sync_role_marker_from_postgres(){
  local actual
  actual="$(pg_actual_role 2>/dev/null || echo unknown)"
  case "$actual" in primary|standby) role_set "$actual";; esac
}

# Never inherit root's Docker client configuration. Mutating cluster commands are
# intentionally re-executed through sudo, and a malformed /root/.docker/config.json
# must not leak warnings or break Docker calls. Use the bundle-local registry config
# instead; this directory is also safe for non-root status/doctor commands.
setup_docker_cli_config(){
  local dir="$ROOT/.docker" owner group
  owner="$(owner_user)"; group="$(owner_group)"
  if [ -e "$dir" ] && [ ! -d "$dir" ]; then
    die "$dir exists but is not a directory"
  fi
  if [ "$(id -u)" -eq 0 ]; then
    install -d -m 700 -o "$owner" -g "$group" "$dir"
  else
    mkdir -p "$dir"
    chmod 700 "$dir" 2>/dev/null || true
  fi
  if [ -f "$ROOT/config.json" ]; then
    cp -f "$ROOT/config.json" "$dir/config.json"
    chmod 600 "$dir/config.json" 2>/dev/null || true
    [ "$(id -u)" -ne 0 ] || chown "$owner:$group" "$dir/config.json"
  elif [ -e "$dir/config.json" ] && [ ! -f "$dir/config.json" ]; then
    # The project-local path is managed by TaskForge; a directory here is never valid.
    rm -rf "$dir/config.json"
  fi
  export DOCKER_CONFIG="$dir"
}
setup_docker_cli_config

cmd_cpu_profile(){
  local write=0 arch profile image missing
  [ "${1:-}" != --write ] || write=1
  arch="$(uname -m)"; profile=standard; image='minio/minio:RELEASE.2025-04-22T22-12-26Z'
  if [ "$arch" = x86_64 ]; then
    missing="$(python3 - <<'PY'
from pathlib import Path
flags=set()
for line in Path('/proc/cpuinfo').read_text(errors='ignore').lower().splitlines():
    if line.startswith('flags') and ':' in line: flags.update(line.split(':',1)[1].split())
required={'cx16','lahf_lm','popcnt','ssse3','sse4_1','sse4_2'}
print(' '.join(sorted(required-flags)))
PY
)"
    if [ -n "$missing" ] || [ "${TASKFORGE_FORCE_MINIO_CPUV1:-0}" = 1 ]; then
      profile=x86-64-v1; image='minio/minio:RELEASE.2025-04-22T22-12-26Z-cpuv1'
    fi
  fi
  printf 'architecture=%s\nprofile=%s\nminio_image=%s\n' "$arch" "$profile" "$image"
  if [ "$write" -eq 1 ]; then
    prepare_runtime
    printf 'TASKFORGE_CPU_PROFILE=%s\nMINIO_IMAGE=%s\n' "$profile" "$image" > "$RUNTIME/cpu.env"
    chmod 600 "$RUNTIME/cpu.env"; [ "$(id -u)" -ne 0 ] || fix_owner "$RUNTIME/cpu.env"
  fi
}

ensure_wg_key(){
  need_root; need wg
  install -d -m 700 /etc/wireguard
  if [ ! -s /etc/wireguard/taskforge-private.key ]; then umask 077; wg genkey > /etc/wireguard/taskforge-private.key; fi
  wg pubkey < /etc/wireguard/taskforge-private.key > /etc/wireguard/taskforge-public.key
  chmod 600 /etc/wireguard/taskforge-private.key; chmod 644 /etc/wireguard/taskforge-public.key
}

cmd_prepare_node(){
  need_root; auto_host_deps; ensure_wg_key
  local node='' public_ip='' wg_ip='' priority=50 wg_port=51820 http_port=80 https_port=443 pg_port=5432 minio_port=9000 minio_console=9001 health_port=9187 voter=0 output=''
  while [ $# -gt 0 ]; do
    case "$1" in
      --node) shift; node="${1:-}";; --public-ip) shift; public_ip="${1:-}";; --wg-ip) shift; wg_ip="${1:-}";;
      --priority) shift; priority="${1:-}";; --wg-port) shift; wg_port="${1:-}";; --http-port) shift; http_port="${1:-}";;
      --https-port) shift; https_port="${1:-}";; --postgres-port) shift; pg_port="${1:-}";; --minio-port) shift; minio_port="${1:-}";;
      --minio-console-port) shift; minio_console="${1:-}";; --health-port) shift; health_port="${1:-}";; --quorum-voter) voter=1;;
      --output) shift; output="${1:-}";; *) die "unknown option $1";;
    esac; shift
  done
  [ -n "$node" ] && [ -n "$public_ip" ] && [ -n "$wg_ip" ] || die 'usage: prepare-node --node C --public-ip IP --wg-ip 10.80.0.3 [ports]'
  output="${output:-$ROOT/taskforge-node-$node.json}"
  args=(--node "$node" --public-ip "$public_ip" --wg-ip "$wg_ip" --public-key-file /etc/wireguard/taskforge-public.key --priority "$priority" --wg-port "$wg_port" --http-port "$http_port" --https-port "$https_port" --postgres-port "$pg_port" --minio-port "$minio_port" --minio-console-port "$minio_console" --health-port "$health_port" --output "$output")
  [ "$voter" -eq 0 ] || args+=(--quorum-voter)
  python3 "$MANAGER" prepare-descriptor "${args[@]}" >/dev/null
  chmod 600 "$output"; fix_owner "$output"
  echo "Node descriptor: $output"
  echo "Copy it to A and run: bash ./cluster.sh add-node $(basename "$output")"
}

cmd_add_node(){
  prepare_inventory
  local descriptor="${1:-}"; [ -s "$descriptor" ] || die 'usage: add-node taskforge-node-C.json'
  mgr add-node "$descriptor"
  chmod 600 "$INVENTORY"; [ "$(id -u)" -ne 0 ] || fix_owner "$INVENTORY"
  cp "$INVENTORY" "$ROOT/taskforge-cluster-topology.json"; chmod 600 "$ROOT/taskforge-cluster-topology.json"
  [ "$(id -u)" -ne 0 ] || fix_owner "$ROOT/taskforge-cluster-topology.json"
  echo "Topology export: $ROOT/taskforge-cluster-topology.json"
}

cmd_import_topology(){
  need_root
  local file="${1:-}"; [ -s "$file" ] || die 'usage: import-topology taskforge-cluster-topology.json'
  python3 "$MANAGER" --inventory "$file" validate >/dev/null
  cp "$file" "$INVENTORY"; chmod 600 "$INVENTORY"; fix_owner "$INVENTORY"
  echo 'Topology imported. Run bash ./cluster.sh apply NODE_ID.'
}

apply_wireguard(){
  local node="$1" actual expected expected_ip deadline
  ensure_wg_key
  actual="$(cat /etc/wireguard/taskforge-public.key)"; expected="$(node_field "$node" 'd["wireguard"]["public_key"]')"
  expected_ip="$(node_field "$node" 'd["wireguard"]["ip"]')"
  [ "$actual" = "$expected" ] || die "WireGuard key mismatch for $node. inventory=$expected local=$actual"
  mgr render-wireguard --node "$node" --private-key-file /etc/wireguard/taskforge-private.key --output /etc/wireguard/wg-taskforge.conf >/dev/null
  chmod 600 /etc/wireguard/wg-taskforge.conf
  systemctl enable wg-quick@wg-taskforge >/dev/null
  systemctl restart wg-quick@wg-taskforge
  deadline=$((SECONDS+10))
  while [ "$SECONDS" -le "$deadline" ]; do
    if systemctl is-active --quiet wg-quick@wg-taskforge       && ip -o addr show dev wg-taskforge 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | grep -Fxq "$expected_ip"; then
      return 0
    fi
    sleep 0.2
  done
  systemctl --no-pager --full status wg-quick@wg-taskforge >&2 || true
  ip -br addr show wg-taskforge >&2 || true
  die "WireGuard wg-taskforge did not become ready with $expected_ip"
}

apply_firewall(){
  local node="$1" local_ip local_wg_port pg_port minio_port health http_port https_port patroni_port etcd_client_port etcd_peer_port
  local rabbitmq_port redis_port browser_cluster_port image_analyzer_cluster_port
  local peer public_host peer_ip public_ip

  command -v ufw >/dev/null 2>&1 || die 'ufw is required; host bootstrap should install it automatically'

  local_ip="$(node_field "$node" 'd["wireguard"]["ip"]')"
  local_wg_port="$(node_field "$node" 'd["wireguard"]["listen_port"]')"
  pg_port="$(node_field "$node" 'd["postgres"].get("cluster_port",5432)')"
  minio_port="$(node_field "$node" 'd["minio"].get("cluster_port",9000)')"
  health="$(node_field "$node" 'd.get("health_port",9187)')"
  patroni_port="$(node_field "$node" 'd["postgres"].get("patroni_rest_port",8008)')"
  etcd_client_port="$(node_field "$node" 'd.get("etcd",{}).get("client_port",2379)')"
  etcd_peer_port="$(node_field "$node" 'd.get("etcd",{}).get("peer_port",2380)')"
  http_port="$(node_field "$node" 'd["web"]["http_port"]')"
  https_port="$(node_field "$node" 'd["web"]["https_port"]')"
  rabbitmq_port="$(node_field "$node" 'd.get("rabbitmq_port",5672)')"
  redis_port="$(node_field "$node" 'd.get("redis_port",6379)')"
  browser_cluster_port="$(node_field "$node" 'd.get("browser_cluster_port",18080)')"
  image_analyzer_cluster_port="$(node_field "$node" 'd.get("image_analyzer_cluster_port",18090)')"

  # Stage SSH and public web rules *before* enabling UFW. This makes first
  # deployment safe over SSH and prevents the C-node situation where UFW was
  # left disabled forever merely because it started inactive.
  firewall_allow_ssh || die 'cannot stage SSH firewall rules; refusing to enable UFW'
  firewall_allow_public_web "$http_port" "$https_port" || die 'cannot stage public web firewall rules'

  while IFS='|' read -r peer public_host peer_ip; do
    [ -n "$peer" ] || continue
    public_ip="$public_host"
    [[ "$public_ip" =~ ^[0-9]+(\.[0-9]+){3}$ ]] || public_ip="$(getent ahostsv4 "$public_host" | awk 'NR==1{print $1}')"
    [ -n "$public_ip" ] || die "cannot resolve $public_host"
    ufw allow from "$public_ip" to any port "$local_wg_port" proto udp comment "TaskForge WG $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$pg_port" proto tcp comment "TaskForge PostgreSQL $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$minio_port" proto tcp comment "TaskForge MinIO $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$health" proto tcp comment "TaskForge health $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$patroni_port" proto tcp comment "TaskForge Patroni $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$etcd_client_port" proto tcp comment "TaskForge etcd client $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$etcd_peer_port" proto tcp comment "TaskForge etcd peer $peer" >/dev/null
    # Warm/lite application nodes use the active node's stateful services and
    # may proxy heavy services to it. Keep these ports private to WireGuard peers.
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$rabbitmq_port" proto tcp comment "TaskForge RabbitMQ $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$redis_port" proto tcp comment "TaskForge Redis $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$browser_cluster_port" proto tcp comment "TaskForge browser upstream $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$image_analyzer_cluster_port" proto tcp comment "TaskForge image analyzer upstream $peer" >/dev/null
  done < <(python3 - "$INVENTORY" "$node" <<'PY_FW_PEERS'
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
for n in cfg['nodes']:
    if n['id'] != sys.argv[2]:
        print(f"{n['id']}|{n['public_host']}|{n['wireguard']['ip']}")
PY_FW_PEERS
)

  firewall_enable_if_needed || die 'failed to enable UFW safely'
  if firewall_is_active; then
    info "UFW active; SSH preserved, web source=$TASKFORGE_FIREWALL_WEB_SOURCE, WireGuard/data rules applied"
  else
    warn 'UFW remains inactive because automatic enable was explicitly disabled'
  fi
}

cluster_endpoint_ready(){
  local name="$1" bind_ip="$2" cluster_port="$3"
  case "$name" in
    postgres)
      timeout 4 bash -c "</dev/tcp/$bind_ip/$cluster_port" >/dev/null 2>&1
      ;;
    minio)
      curl -fsS --connect-timeout 3 --max-time 5         "http://$bind_ip:$cluster_port/minio/health/ready" >/dev/null 2>&1
      ;;
    *) return 2 ;;
  esac
}

install_proxy(){
  local name bind_ip cluster_port local_port result
  name="$1"
  bind_ip="$2"
  cluster_port="$3"
  local_port="$4"
  if ! result="$(proxy_reconcile "$name" "$bind_ip" "$cluster_port" "$local_port")"; then
    warn "cannot reconcile $name cluster endpoint $bind_ip:$cluster_port"
    return 1
  fi
  case "$result" in
    managed-existing)
      info "$name proxy already active on $bind_ip:$cluster_port -> 127.0.0.1:$local_port"
      ;;
    managed-created)
      info "$name proxy $bind_ip:$cluster_port -> 127.0.0.1:$local_port"
      ;;
    direct)
      info "$name direct listener preserved on $bind_ip:$cluster_port"
      ;;
    *)
      warn "unexpected $name proxy reconciliation result: ${result:-<empty>}"
      return 1
      ;;
  esac
  if ! cluster_endpoint_ready "$name" "$bind_ip" "$cluster_port"; then
    warn "$name endpoint is listening but failed service readiness check at $bind_ip:$cluster_port"
    "$TASKFORGE_SS" -lntp 2>/dev/null | grep -F "$bind_ip:$cluster_port" >&2 || true
    return 1
  fi
}

apply_proxies(){
  local node="$1" ip pg_local pg_cluster mi_local mi_cluster failures=0
  ip="$(node_field "$node" 'd["wireguard"]["ip"]')"
  pg_local="$(node_field "$node" 'd["postgres"]["local_port"]')"
  pg_cluster="$(node_field "$node" 'd["postgres"].get("cluster_port",5432)')"
  mi_local="$(node_field "$node" 'd["minio"]["local_port"]')"
  mi_cluster="$(node_field "$node" 'd["minio"].get("cluster_port",9000)')"

  # Reconcile both endpoints even when one fails. v36 aborted immediately on a
  # transient PostgreSQL readiness race and therefore never recreated MinIO,
  # leaving a half-repaired node. v37 always attempts the complete pair first.
  install_proxy postgres "$ip" "$pg_cluster" "$pg_local" || failures=$((failures+1))
  install_proxy minio "$ip" "$mi_cluster" "$mi_local" || failures=$((failures+1))
  [ "$failures" -eq 0 ] || die "cannot reconcile $failures cluster endpoint(s) on node $node; inspect systemctl status taskforge-*-wg-proxy.service"
}

cmd_apply(){
  need_root; auto_host_deps; need_env; prepare_inventory; need wg; need socat; need docker
  local node="${1:-}"; [ -n "$node" ] || die 'usage: apply NODE_ID'
  mgr set-node "$node" >/dev/null; mgr render-local --node "$node" >/dev/null; cmd_cpu_profile --write >/dev/null
  apply_wireguard "$node"; apply_firewall "$node"; apply_proxies "$node"
  # Existing PostgreSQL nodes must immediately trust every peer from the
  # updated inventory for physical replication. This is what makes adding a
  # new C/D node truly one-command: after A/B import the topology and run
  # apply, the new standby can pg_basebackup without a manual pg_hba.conf edit.
  if docker inspect "$(pg_container)" >/dev/null 2>&1; then
    configure_local_hba "$node"
  fi
  prepare_runtime; fix_owner "$ROOT/.runtime"
  sudo -u "$(owner_user)" "$CHECK"
  echo "Node $node applied. No volume was created, deleted or promoted."
}

cmd_adopt(){
  need_root; need_env; prepare_inventory
  local node="${1:-}"; [ -n "$node" ] || die 'usage: adopt NODE_ID'
  cmd_apply "$node"
  local pg role=unconfigured recovery receiver
  pg="$(pg_container)"
  if docker inspect "$pg" >/dev/null 2>&1; then
    recovery="$(pg_scalar "$pg" 'select pg_is_in_recovery()' 2>/dev/null || true)"
    [ "$recovery" != f ] || role=primary; [ "$recovery" != t ] || role=standby
    if [ "$role" = standby ]; then receiver="$(pg_scalar "$pg" 'select status from pg_stat_wal_receiver limit 1' 2>/dev/null || true)"; [ "$receiver" = streaming ] || warn "standby receiver=$receiver"; fi
  fi
  if docker inspect "$(minio_container)" >/dev/null 2>&1; then
    minio_prepare_local "$node"
  fi
  if [ "$role" = standby ]; then
    compose up -d --no-deps rabbitmq redis
    wait_health "$(container rabbitmq)" 180
    wait_health "$(container redis)" 120
  fi
  role_set "$role"; printf 'adopted %s node=%s role=%s\n' "$(date -u +%FT%TZ)" "$node" "$role" > "$EASY_RUNTIME/adopted"; chmod 600 "$EASY_RUNTIME/adopted"; fix_owner "$EASY_RUNTIME/adopted"
  echo "Node $node adopted: PostgreSQL role=$role. Existing PostgreSQL/MinIO containers and volumes were not recreated."
  cmd_status
}

configure_local_hba(){
  local node="$1" pg="$(pg_container)" peer_csv
  peer_csv="$(python3 - "$INVENTORY" "$node" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
print(','.join(n['wireguard']['ip'] for n in cfg['nodes'] if n['id'] != sys.argv[2]))
PY
)"
  docker exec -e TASKFORGE_PEER_IPS="$peer_csv" "$pg" sh -ceu '
HBA="$(psql -X -U "$POSTGRES_USER" -d postgres -Atc "show hba_file")"
for ip in $(printf "%s" "$TASKFORGE_PEER_IPS" | tr "," " "); do
  rule="host replication ${POSTGRES_USER} ${ip}/32 scram-sha-256"
  grep -Fqx "$rule" "$HBA" || printf "%s\n" "$rule" >> "$HBA"
done
psql -X -U "$POSTGRES_USER" -d postgres -c "select pg_reload_conf()" >/dev/null
'
}

cmd_init_primary(){
  need_root; need_env; prepare_inventory
  local node="${1:-$(primary_id)}"; [ "$node" = "$(primary_id)" ] || die "initial primary must be $(primary_id)"
  cmd_apply "$node"
  if [ -x "$BACKUP" ]; then
    sudo -u "$(owner_user)" "$BACKUP" --metadata
    if docker inspect "$(pg_container)" >/dev/null 2>&1; then
      sudo -u "$(owner_user)" "$BACKUP" --database
    fi
  fi
  compose up -d --no-deps postgres; wait_health "$(pg_container)" 240
  recovery="$(docker exec "$(pg_container)" sh -lc 'psql -X -U "$POSTGRES_USER" -d postgres -Atc "select pg_is_in_recovery()"')"; [ "$recovery" = f ] || die 'preferred PostgreSQL is not primary'
  configure_local_hba "$node"
  compose up -d --no-deps minio rabbitmq redis; wait_health "$(minio_container)" 240; wait_health "$(container rabbitmq)" 180; wait_health "$(container redis)" 120
  apply_proxies "$node"
  minio_prepare_local "$node"
  role_set primary
  info "starting the full TaskForge application stack on the primary"
  compose up -d
  apply_proxies "$node"
  echo "Primary $node is ready and serving TaskForge."
}

install_mc(){
  [ -x "$RUNTIME/bin/mc" ] || "$CLUSTER_DIR/ops/host/install-mc.sh" >/dev/null
  if [ "$(id -u)" -eq 0 ]; then chown "$(owner_pair)" "$RUNTIME/bin/mc"; fi
  chmod 700 "$RUNTIME/bin/mc"
}
minio_prepare_local(){
  local node="$1" user pass data_bucket state_bucket bucket port mc
  local -a local_buckets
  install_mc; mc="$RUNTIME/bin/mc"; export MC_CONFIG_DIR="$RUNTIME/mc-easy"; mkdir -p "$MC_CONFIG_DIR"; chmod 700 "$MC_CONFIG_DIR"; [ "$(id -u)" -ne 0 ] || fix_owner "$MC_CONFIG_DIR"
  user="$(env_get MINIO_ROOT_USER)"; user="${user:-taskforge}"; pass="$(env_get MINIO_ROOT_PASSWORD)"
  data_bucket="$(env_get S3_BUCKET)"; data_bucket="${data_bucket:-taskforge-files}"
  state_bucket='taskforge-cluster-state'
  port="$(node_field "$node" 'd["minio"]["local_port"]')"
  "$mc" alias set local "http://127.0.0.1:$port" "$user" "$pass" >/dev/null
  local_buckets=("$data_bucket" "$state_bucket")
  for bucket in "${local_buckets[@]}"; do
    "$mc" mb --ignore-existing --with-versioning "local/$bucket" >/dev/null
    "$mc" version enable "local/$bucket" >/dev/null
  done
}

cmd_sync_minio(){
  need_env; prepare_inventory
  local assume=0 test_rules=1
  while [ $# -gt 0 ]; do
    case "$1" in
      --yes) assume=1;;
      --no-test) test_rules=0;;
      *) die 'usage: sync-minio --yes [--no-test]';;
    esac
    shift
  done
  [ "$assume" -eq 1 ] || die 'add --yes after all configured MinIO nodes are reachable'

  install_mc
  local mc="$RUNTIME/bin/mc" user pass data_bucket state_bucket primary
  local id ip port priority source target target_ip target_port target_priority
  local source_row target_row target_label result replicate_features bucket
  local tmp stamp key needle probe_prefix cleanup_id cleanup_bucket
  export MC_CONFIG_DIR="$RUNTIME/mc-easy"
  mkdir -p "$MC_CONFIG_DIR"; chmod 700 "$MC_CONFIG_DIR"; [ "$(id -u)" -ne 0 ] || fix_owner "$MC_CONFIG_DIR"
  user="$(env_get MINIO_ROOT_USER)"; user="${user:-taskforge}"
  pass="$(env_get MINIO_ROOT_PASSWORD)"
  data_bucket="$(env_get S3_BUCKET)"; data_bucket="${data_bucket:-taskforge-files}"
  state_bucket='taskforge-cluster-state'
  primary="$(primary_id)"
  replicate_features='delete,delete-marker,existing-objects,metadata-sync'
  buckets=("$data_bucket" "$state_bucket")

  mapfile -t rows < <(python3 - "$INVENTORY" <<'PY_ROWS'
import json,sys
for n in json.load(open(sys.argv[1],encoding='utf-8'))['nodes']:
    print(f"{n['id']}|{n['wireguard']['ip']}|{n['minio'].get('cluster_port',9000)}|{int(n['priority'])}")
PY_ROWS
)
  ids=()
  for row in "${rows[@]}"; do
    IFS='|' read -r id ip port priority <<<"$row"
    ids+=("$id")
    "$mc" alias set "tf-$id" "http://$ip:$port" "$user" "$pass" >/dev/null
    "$mc" admin info "tf-$id" >/dev/null || die "MinIO $id unreachable at $ip:$port"
    for bucket in "${buckets[@]}"; do
      "$mc" mb --ignore-existing --with-versioning "tf-$id/$bucket" >/dev/null
      "$mc" version enable "tf-$id/$bucket" >/dev/null
    done
  done

  # Seed only an empty replica. Never auto-merge independent histories.
  for bucket in "${buckets[@]}"; do
    for id in "${ids[@]}"; do
      [ "$id" = "$primary" ] && continue
      if ! "$mc" ls --recursive "tf-$id/$bucket" 2>/dev/null | grep -q .; then
        "$mc" mirror --overwrite "tf-$primary/$bucket" "tf-$id/$bucket" >/dev/null
      fi
    done
  done

  # Each source bucket requires unique rule priorities. The target node priority
  # is deterministic and already validated by the inventory manager.
  for bucket in "${buckets[@]}"; do
    for source_row in "${rows[@]}"; do
      IFS='|' read -r source _ _ _ <<<"$source_row"
      for target_row in "${rows[@]}"; do
        IFS='|' read -r target target_ip target_port target_priority <<<"$target_row"
        [ "$source" = "$target" ] && continue
        target_label="$target_ip:$target_port/$bucket"
        if ! result="$(minio_add_rule_verified "$mc" "tf-$source/$bucket" "tf-$target/$bucket" "$target_label" "$target_priority" "$replicate_features")"; then
          die "cannot configure verified MinIO rule $source -> $target bucket=$bucket (priority=$target_priority)"
        fi
        if [ "$result" = created ]; then
          info "MinIO rule $source -> $target bucket=$bucket configured priority=$target_priority"
        else
          info "MinIO rule $source -> $target bucket=$bucket already present"
        fi
      done
    done
  done

  if [ "$test_rules" -eq 1 ] && [ "${#ids[@]}" -ge 2 ]; then
    tmp="$(mktemp)"
    probe_prefix="__cluster_probe__"
    cleanup_minio_probe(){
      rm -f "$tmp"
      for cleanup_id in "${ids[@]}"; do
        for cleanup_bucket in "${buckets[@]}"; do
          "$mc" rm --recursive --force --versions \
            "tf-$cleanup_id/$cleanup_bucket/$probe_prefix/" >/dev/null 2>&1 || true
        done
      done
    }
    trap cleanup_minio_probe RETURN
    stamp="$(date +%s)-$$"
    printf 'TaskForge replication probe %s\n' "$stamp" > "$tmp"
    for bucket in "${buckets[@]}"; do
      for source in "${ids[@]}"; do
        key="$probe_prefix/$stamp-$bucket-$source.txt"
        needle="$(basename "$key")"
        "$mc" cp "$tmp" "tf-$source/$bucket/$key" >/dev/null
        for target in "${ids[@]}"; do
          [ "$source" = "$target" ] && continue
          if minio_wait_local_object "$mc" "tf-$target/$bucket/$probe_prefix/" "$needle" 60; then
            info "MinIO probe $source -> $target bucket=$bucket passed"
          else
            die "MinIO probe $source -> $target bucket=$bucket failed"
          fi
        done
      done
    done
    cleanup_minio_probe
    trap - RETURN
  fi
}

cmd_join(){
  need_root; need_env; prepare_inventory
  local node='' primary="$(primary_id)" assume=0 reset=0
  while [ $# -gt 0 ]; do case "$1" in --node) shift; node="${1:-}";; --primary) shift; primary="${1:-}";; --yes) assume=1;; --reset-data) reset=1;; *) [ -z "$node" ] && node="$1" || die "unknown option $1";; esac; shift; done
  [ -n "$node" ] || die 'usage: join NODE_ID [--primary A] --yes [--reset-data]'; [ "$assume" -eq 1 ] || die 'add --yes'; [ "$node" != "$primary" ] || die 'use init-primary on the preferred primary'
  cmd_apply "$node"
  local pg_host pg_port pg_user pg_password slot pg volume_name existing=0 recovery receiver nonempty peer_list
  pg_host="$(node_field "$primary" 'd["wireguard"]["ip"]')"; pg_port="$(node_field "$primary" 'd["postgres"].get("cluster_port",5432)')"; pg_user="$(env_get POSTGRES_USER)"; pg_user="${pg_user:-taskforge}"; pg_password="$(env_get POSTGRES_PASSWORD)"; slot="taskforge_$(printf '%s' "$node" | tr '[:upper:]-' '[:lower:]_')"; pg="$(pg_container)"; volume_name="$(volume postgres-data)"
  if docker inspect "$pg" >/dev/null 2>&1; then recovery="$(pg_scalar "$pg" 'select pg_is_in_recovery()' 2>/dev/null || true)"; receiver="$(pg_scalar "$pg" 'select status from pg_stat_wal_receiver limit 1' 2>/dev/null || true)"; [ "$recovery" = t ] && [ "$receiver" = streaming ] && existing=1; fi
  if [ "$existing" -eq 0 ]; then
    if docker volume inspect "$volume_name" >/dev/null 2>&1; then
      nonempty="$(docker run --rm -v "$volume_name:/data" alpine:3.22 sh -c 'find /data -mindepth 1 -print -quit' 2>/dev/null || true)"
      if [ -n "$nonempty" ] && [ "$reset" -ne 1 ]; then die "volume $volume_name is non-empty; use --reset-data only for a failed join with no unique data"; fi
      if [ "$reset" -eq 1 ]; then compose rm -s -f postgres >/dev/null 2>&1 || true; docker volume rm "$volume_name" >/dev/null; fi
    fi
    docker volume inspect "$volume_name" >/dev/null 2>&1 || docker volume create --label "com.docker.compose.project=$(project)" --label 'com.docker.compose.volume=postgres-data' "$volume_name" >/dev/null
    timeout 8 bash -c "</dev/tcp/$pg_host/$pg_port" || die "primary PostgreSQL unreachable at $pg_host:$pg_port"
    slot_exists="$(docker run --rm --network host -e PGPASSWORD="$pg_password" postgres:18-alpine psql -X -h "$pg_host" -p "$pg_port" -U "$pg_user" -d postgres -Atc "select 1 from pg_replication_slots where slot_name='$slot'")"
    if [ "$slot_exists" != 1 ]; then
      docker run --rm --network host -e PGPASSWORD="$pg_password" postgres:18-alpine psql -X -h "$pg_host" -p "$pg_port" -U "$pg_user" -d postgres -v ON_ERROR_STOP=1 -c "select * from pg_create_physical_replication_slot('$slot');" >/dev/null
    fi
    info "copying PostgreSQL $primary -> $node"
    docker run --rm --network host -e PGPASSWORD="$pg_password" -v "$volume_name:/var/lib/postgresql" postgres:18-alpine sh -ceu '
mkdir -p /var/lib/postgresql/18/docker; chown -R postgres:postgres /var/lib/postgresql
exec gosu postgres pg_basebackup -h "$1" -p "$2" -U "$3" -D /var/lib/postgresql/18/docker -Fp -Xs -P -R -S "$4"
' sh "$pg_host" "$pg_port" "$pg_user" "$slot"
    peer_list="$(python3 - "$INVENTORY" "$node" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
print(' '.join(n['wireguard']['ip'] for n in cfg['nodes'] if n['id'] != sys.argv[2]))
PY
)"
    docker run --rm -v "$volume_name:/var/lib/postgresql" alpine:3.22 sh -ceu 'hba=/var/lib/postgresql/18/docker/pg_hba.conf; for ip in $1; do rule="host replication $2 ${ip}/32 scram-sha-256"; grep -Fqx "$rule" "$hba" || printf "%s\n" "$rule" >> "$hba"; done' sh "$peer_list" "$pg_user"
    compose up -d --no-deps postgres; wait_health "$pg" 300
  fi
  recovery="$(docker exec "$pg" sh -lc 'psql -X -U "$POSTGRES_USER" -d postgres -Atc "select pg_is_in_recovery()"')"; receiver="$(pg_scalar "$pg" 'select status from pg_stat_wal_receiver limit 1')"; [ "$recovery" = t ] && [ "$receiver" = streaming ] || die 'PostgreSQL is not a streaming standby'
  # PostgreSQL role is factual state, not a reward for finishing MinIO setup.
  role_set standby
  compose up -d --no-deps minio rabbitmq redis; wait_health "$(minio_container)" 240; wait_health "$(container rabbitmq)" 180; wait_health "$(container redis)" 120
  cmd_sync_minio --yes
  printf 'joined %s primary=%s slot=%s\n' "$(date -u +%FT%TZ)" "$primary" "$slot" > "$EASY_RUNTIME/joined"; chmod 600 "$EASY_RUNTIME/joined"; fix_owner "$EASY_RUNTIME/joined"
  echo "NODE $node READY AS PASSIVE REPLICA"
}


cmd_migrate_local(){
  need_root; auto_host_deps
  local node='' from='' src_env='' src_config='' private_src='' public_src='' owner group
  while [ $# -gt 0 ]; do
    case "$1" in
      --from) shift; from="${1:-}";;
      *) [ -z "$node" ] && node="$1" || die "unknown migrate-local option $1";;
    esac
    shift
  done
  [ -n "$node" ] && [ -n "$from" ] || die 'usage: migrate-local NODE_ID --from /path/to/old-server-folder'
  from="$(cd "$from" 2>/dev/null && pwd)" || die "source folder does not exist: $from"
  if [ -s "$from/.env" ]; then src_env="$from/.env"; elif [ -s "$from/deploy/prod/.env" ]; then src_env="$from/deploy/prod/.env"; else die "no .env found under $from"; fi
  src_config="$from/config.json"
  owner="$(owner_user)"; group="$(owner_group)"

  info "copying local secrets/config from $from"
  cp -a "$src_env" "$ENV_FILE"
  [ ! -f "$src_config" ] || cp -a "$src_config" "$ROOT/config.json"
  mkdir -p "$ROOT/.runtime/code-analyzer-keys"

  private_src="$(python3 - "$ENV_FILE" <<'PY2'
from pathlib import Path
import sys
for raw in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if raw.startswith('CODE_ANALYZER_PRIVATE_KEY_PATH='):
        print(raw.split('=',1)[1].strip().strip('"').strip("'")); break
PY2
)"
  public_src="$(python3 - "$ENV_FILE" <<'PY2'
from pathlib import Path
import sys
for raw in Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    if raw.startswith('CODE_ANALYZER_PUBLIC_KEY_PATH='):
        print(raw.split('=',1)[1].strip().strip('"').strip("'")); break
PY2
)"
  [ -f "$private_src" ] || private_src="$from/.runtime/code-analyzer-keys/code-analyzer-private.pem"
  [ -f "$public_src" ] || public_src="$from/.runtime/code-analyzer-keys/code-analyzer-public.pem"
  [ -f "$private_src" ] || die "Code Analyzer private key not found (env path or $from/.runtime/code-analyzer-keys)"
  [ -f "$public_src" ] || die "Code Analyzer public key not found (env path or $from/.runtime/code-analyzer-keys)"
  cp -a "$private_src" "$ROOT/.runtime/code-analyzer-keys/code-analyzer-private.pem"
  cp -a "$public_src" "$ROOT/.runtime/code-analyzer-keys/code-analyzer-public.pem"

  # HA application startup depends on node-local Origin CA material and shared
  # ASP.NET DataProtection keys. Versioned server folders have independent
  # .runtime trees, so migrate these deliberately instead of discovering the
  # omission only when a standby is promoted.
  mkdir -p "$ROOT/.runtime/cluster/tls" "$ROOT/.runtime/cluster/shared"
  if [ -d "$from/.runtime/cluster/tls" ]; then
    cp -a "$from/.runtime/cluster/tls/." "$ROOT/.runtime/cluster/tls/"
  fi
  if [ -d "$from/.runtime/cluster/shared" ]; then
    cp -a "$from/.runtime/cluster/shared/." "$ROOT/.runtime/cluster/shared/"
  fi

  python3 - "$ENV_FILE" "$ROOT" <<'PY2'
from pathlib import Path
import sys
p=Path(sys.argv[1]); root=Path(sys.argv[2]).resolve()
updates={
 'CODE_ANALYZER_PRIVATE_KEY_PATH': str(root/'.runtime/code-analyzer-keys/code-analyzer-private.pem'),
 'CODE_ANALYZER_PUBLIC_KEY_PATH': str(root/'.runtime/code-analyzer-keys/code-analyzer-public.pem'),
}
lines=p.read_text(encoding='utf-8-sig').splitlines(); seen=set(); out=[]
for raw in lines:
    if '=' in raw and not raw.lstrip().startswith('#'):
        k=raw.split('=',1)[0].strip()
        if k in updates:
            out.append(f'{k}={updates[k]}'); seen.add(k); continue
    out.append(raw)
for k,v in updates.items():
    if k not in seen: out.append(f'{k}={v}')
p.write_text('\n'.join(out)+'\n',encoding='utf-8')
PY2

  chown "$owner:$group" "$ENV_FILE"; chmod 600 "$ENV_FILE"
  [ ! -f "$ROOT/config.json" ] || { chown "$owner:$group" "$ROOT/config.json"; chmod 600 "$ROOT/config.json"; }
  chown -R "$owner:$group" "$ROOT/.runtime"
  chmod 700 "$ROOT/.runtime" "$ROOT/.runtime/code-analyzer-keys" "$ROOT/.runtime/cluster" "$ROOT/.runtime/cluster/tls" 2>/dev/null || true
  chmod 600 "$ROOT/.runtime/code-analyzer-keys/code-analyzer-private.pem"
  chmod 644 "$ROOT/.runtime/code-analyzer-keys/code-analyzer-public.pem"
  chmod 600 "$ROOT/.runtime/cluster/tls/privkey.pem" 2>/dev/null || true
  chmod 644 "$ROOT/.runtime/cluster/tls/fullchain.pem" 2>/dev/null || true

  sudo -u "$owner" "$CHECK"
  info "local migration complete; adopting existing node $node"
  cmd_adopt "$node"
}


cmd_upgrade_v34(){
  need_root; auto_host_deps
  local node='' from='' topology='' candidate
  while [ $# -gt 0 ]; do
    case "$1" in
      --from) shift; from="${1:-}";;
      *) [ -z "$node" ] && node="$1" || die "unknown upgrade-v34 option $1";;
    esac
    shift
  done
  [ -n "$node" ] && [ -n "$from" ] || die 'usage: upgrade-v34 NODE_ID --from /path/to/v34-folder'
  from="$(cd "$from" 2>/dev/null && pwd)" || die "source folder does not exist: $from"
  [ "$from" != "$ROOT" ] || die 'upgrade-v34 source and destination folders must differ'

  for candidate in "$from/cluster/inventory.json" "$from/taskforge-cluster-topology.json"; do
    if [ -s "$candidate" ] && python3 "$MANAGER" --inventory "$candidate" validate >/dev/null 2>&1; then
      topology="$candidate"; break
    fi
  done
  [ -n "$topology" ] || die 'no valid v34 cluster topology found (cluster/inventory.json or taskforge-cluster-topology.json)'

  info "importing v34 topology from $topology"
  cp -a "$topology" "$INVENTORY"; chmod 600 "$INVENTORY"; fix_owner "$INVENTORY"
  cp -a "$topology" "$ROOT/taskforge-cluster-topology.json"; chmod 600 "$ROOT/taskforge-cluster-topology.json"; fix_owner "$ROOT/taskforge-cluster-topology.json"

  cmd_migrate_local "$node" --from "$from"
  prepare_runtime
  printf 'upgraded-v34 %s node=%s source=%s\n' "$(date -u +%FT%TZ)" "$node" "$from" > "$EASY_RUNTIME/upgraded-v34-to-v39"
  chmod 600 "$EASY_RUNTIME/upgraded-v34-to-v39"; fix_owner "$EASY_RUNTIME/upgraded-v34-to-v39"
  echo 'v34 -> v39 local upgrade complete. No PostgreSQL basebackup and no Docker data volume recreation was performed.'
  echo 'Replica-mode WireGuard PostgreSQL/MinIO endpoints were reconciled idempotently.'
  echo 'After every node uses v39, run once if topology/MinIO rules need reconciliation: bash ./cluster.sh finalize-v39 --yes'
}

cmd_upgrade_v35(){
  need_root; auto_host_deps
  local node='' from='' topology='' candidate
  while [ $# -gt 0 ]; do
    case "$1" in
      --from) shift; from="${1:-}";;
      *) [ -z "$node" ] && node="$1" || die "unknown upgrade-v35 option $1";;
    esac
    shift
  done
  [ -n "$node" ] && [ -n "$from" ] || die 'usage: upgrade-v35 NODE_ID --from /path/to/v35-folder'
  from="$(cd "$from" 2>/dev/null && pwd)" || die "source folder does not exist: $from"
  [ "$from" != "$ROOT" ] || die 'upgrade-v35 source and destination folders must differ'

  for candidate in "$from/cluster/inventory.json" "$from/taskforge-cluster-topology.json"; do
    if [ -s "$candidate" ] && python3 "$MANAGER" --inventory "$candidate" validate >/dev/null 2>&1; then
      topology="$candidate"; break
    fi
  done
  [ -n "$topology" ] || die 'no valid v35 cluster topology found (cluster/inventory.json or taskforge-cluster-topology.json)'

  info "importing v35 topology from $topology"
  cp -a "$topology" "$INVENTORY"; chmod 600 "$INVENTORY"; fix_owner "$INVENTORY"
  cp -a "$topology" "$ROOT/taskforge-cluster-topology.json"; chmod 600 "$ROOT/taskforge-cluster-topology.json"; fix_owner "$ROOT/taskforge-cluster-topology.json"

  cmd_migrate_local "$node" --from "$from"
  prepare_runtime
  printf 'upgraded-v35 %s node=%s source=%s\n' "$(date -u +%FT%TZ)" "$node" "$from" > "$EASY_RUNTIME/upgraded-v35-to-v39"
  chmod 600 "$EASY_RUNTIME/upgraded-v35-to-v39"; fix_owner "$EASY_RUNTIME/upgraded-v35-to-v39"
  echo 'v35 -> v39 local upgrade complete. Existing PostgreSQL/MinIO data volumes were preserved.'
  echo 'The replica-mode WireGuard PostgreSQL/MinIO endpoints were reconciled idempotently.'
  echo 'After every node uses v39, run once if topology/MinIO rules need reconciliation: bash ./cluster.sh finalize-v39 --yes'
}

cmd_upgrade_v36(){
  need_root; auto_host_deps
  local node='' from='' topology='' candidate
  while [ $# -gt 0 ]; do
    case "$1" in
      --from) shift; from="${1:-}";;
      *) [ -z "$node" ] && node="$1" || die "unknown upgrade-v36 option $1";;
    esac
    shift
  done
  [ -n "$node" ] && [ -n "$from" ] || die 'usage: upgrade-v36 NODE_ID --from /path/to/v36-folder'
  from="$(cd "$from" 2>/dev/null && pwd)" || die "source folder does not exist: $from"
  [ "$from" != "$ROOT" ] || die 'upgrade-v36 source and destination folders must differ'

  for candidate in "$from/cluster/inventory.json" "$from/taskforge-cluster-topology.json"; do
    if [ -s "$candidate" ] && python3 "$MANAGER" --inventory "$candidate" validate >/dev/null 2>&1; then
      topology="$candidate"; break
    fi
  done
  [ -n "$topology" ] || die 'no valid v36 cluster topology found (cluster/inventory.json or taskforge-cluster-topology.json)'

  info "importing v36 topology from $topology"
  cp -a "$topology" "$INVENTORY"; chmod 600 "$INVENTORY"; fix_owner "$INVENTORY"
  cp -a "$topology" "$ROOT/taskforge-cluster-topology.json"; chmod 600 "$ROOT/taskforge-cluster-topology.json"; fix_owner "$ROOT/taskforge-cluster-topology.json"

  cmd_migrate_local "$node" --from "$from"
  prepare_runtime
  printf 'upgraded-v36 %s node=%s source=%s\n' "$(date -u +%FT%TZ)" "$node" "$from" > "$EASY_RUNTIME/upgraded-v36-to-v39"
  chmod 600 "$EASY_RUNTIME/upgraded-v36-to-v39"; fix_owner "$EASY_RUNTIME/upgraded-v36-to-v39"
  echo 'v36 -> v39 local upgrade complete. Existing PostgreSQL/MinIO data volumes were preserved.'
  echo 'Audited endpoint reconciliation waits for real readiness and always attempts both PostgreSQL and MinIO endpoints.'
  echo 'After every node uses v39, run once if topology/MinIO rules need reconciliation: bash ./cluster.sh finalize-v39 --yes'
}

validate_v40_app_material(){
  local node="$1" profile tls_mode
  profile="$(node_field "$node" '((d.get("app") or {}).get("profile","full"))')"
  tls_mode="$(node_field "$node" 'd.get("web",{}).get("tls_mode","origin-ca")')"
  if [ "$profile" != none ] && [ "$tls_mode" = origin-ca ]; then
    [ -s "$ROOT/.runtime/cluster/tls/fullchain.pem" ] \
      || die "node $node app profile=$profile requires Origin TLS certificate: missing .runtime/cluster/tls/fullchain.pem"
    [ -s "$ROOT/.runtime/cluster/tls/privkey.pem" ] \
      || die "node $node app profile=$profile requires Origin TLS private key: missing .runtime/cluster/tls/privkey.pem"
  fi
}

normalize_v40_app_profiles(){
  python3 - "$INVENTORY" <<'PY_V40_PROFILES'
import json,sys
from pathlib import Path
p=Path(sys.argv[1])
cfg=json.loads(p.read_text(encoding='utf-8'))
ranked=sorted(cfg.get('nodes',[]), key=lambda n:int(n.get('priority',0)), reverse=True)
rank={str(n['id']):i for i,n in enumerate(ranked)}
voters=[n for n in cfg.get('nodes',[]) if n.get('quorum_voter',False)]
if len(voters) < 3 or len(voters) % 2 == 0:
    # v40's default A/B/C design uses the three highest-priority nodes as the
    # consensus voters. Extra future nodes stay non-voters until explicitly
    # promoted into the DCS topology.
    for n in cfg.get('nodes',[]):
        n['quorum_voter'] = rank[str(n['id'])] < 3
for n in cfg.get('nodes',[]):
    if isinstance(n.get('app'),dict):
        continue
    if rank[str(n['id'])] < 2:
        n['app']={
            'profile':'full',
            'can_be_primary':True,
            'assist_on_failover':False,
            'exclude_services':[],
            'assist_exclude_services':[],
        }
    else:
        n['app']={
            'profile':'lite',
            'can_be_primary':False,
            'assist_on_failover':True,
            'exclude_services':['browser-api','image-analyzer'],
            'assist_exclude_services':['support-bot','telegram-quiz-bot','rating-worker'],
        }
p.write_text(json.dumps(cfg,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
PY_V40_PROFILES
  chmod 600 "$INVENTORY"
  fix_owner "$INVENTORY"
}

cmd_upgrade_v39(){
  need_root; auto_host_deps
  local node='' from='' topology='' candidate
  while [ $# -gt 0 ]; do
    case "$1" in
      --from) shift; from="${1:-}";;
      *) [ -z "$node" ] && node="$1" || die "unknown upgrade-v39 option $1";;
    esac
    shift
  done
  [ -n "$node" ] && [ -n "$from" ] || die 'usage: upgrade-v39 NODE_ID --from /path/to/v39-folder'
  from="$(cd "$from" 2>/dev/null && pwd)" || die "source folder does not exist: $from"
  [ "$from" != "$ROOT" ] || die 'upgrade-v39 source and destination folders must differ'

  for candidate in "$from/cluster/inventory.json" "$from/taskforge-cluster-topology.json"; do
    if [ -s "$candidate" ] && python3 "$MANAGER" --inventory "$candidate" validate >/dev/null 2>&1; then
      topology="$candidate"; break
    fi
  done
  [ -n "$topology" ] || die 'no valid v39 cluster topology found (cluster/inventory.json or taskforge-cluster-topology.json)'

  info "importing v39 topology from $topology"
  cp -a "$topology" "$INVENTORY"; chmod 600 "$INVENTORY"; fix_owner "$INVENTORY"
  normalize_v40_app_profiles
  cp -a "$INVENTORY" "$ROOT/taskforge-cluster-topology.json"; chmod 600 "$ROOT/taskforge-cluster-topology.json"; fix_owner "$ROOT/taskforge-cluster-topology.json"

  cmd_migrate_local "$node" --from "$from"
  validate_v40_app_material "$node"

  # Build the v40 application/telemetry topology even before the PostgreSQL
  # quorum migration. The node agent then runs in safe replica-monitor mode:
  # A is observed, B/C keep only their assigned images warm, and no automatic
  # promotion is attempted until the explicit Patroni/etcd transition.
  mgr render-quorum --output "$CLUSTER_DIR/cluster.json" >/dev/null
  chmod 600 "$CLUSTER_DIR/cluster.json"; fix_owner "$CLUSTER_DIR/cluster.json"
  "$CLUSTER_DIR/ops/quorum/install-service.sh" >/dev/null
  local_ip="$(node_field "$node" 'd["wireguard"]["ip"]')"
  health_port="$(node_field "$node" 'd.get("health_port",9187)')"
  cluster_wait_deadline=$((SECONDS+45))
  while [ "$SECONDS" -lt "$cluster_wait_deadline" ]; do
    if curl -fsS --connect-timeout 2 --max-time 3 "http://$local_ip:$health_port/ha/live" >/dev/null 2>&1; then break; fi
    sleep 1
  done
  curl -fsS --connect-timeout 2 --max-time 3 "http://$local_ip:$health_port/ha/live" >/dev/null 2>&1 \
    || die "v40 node agent did not become ready at $local_ip:$health_port"

  # Apply the new routing/telemetry environment to the currently active node.
  # This is a small targeted recreate, not a full deployment. Standby nodes have
  # no application containers yet and will receive the same environment when
  # the HA agent starts their assigned profile.
  if [ "$(pg_actual_role 2>/dev/null || echo unknown)" = primary ]; then
    info 'refreshing gateway/tasks/observability runtime environment on the active node'
    compose up -d --no-deps gateway tasks-api observability-api
  fi

  prepare_runtime
  printf 'upgraded-v39 %s node=%s source=%s\n' "$(date -u +%FT%TZ)" "$node" "$from" > "$EASY_RUNTIME/upgraded-v39-to-v40"
  chmod 600 "$EASY_RUNTIME/upgraded-v39-to-v40"; fix_owner "$EASY_RUNTIME/upgraded-v39-to-v40"
  echo 'v39 -> v40 local upgrade complete. PostgreSQL/MinIO data volumes were preserved.'
  echo 'Node telemetry is active immediately. In replica mode B/C PREPARE/STOP their assigned app profile and Watchtower continuously updates those stopped containers.'
  echo 'Automatic promotion/application failover remains disabled until the explicit quorum migration is completed.'
}

cmd_upgrade_v38(){
  need_root; auto_host_deps
  local node='' from='' topology='' candidate
  while [ $# -gt 0 ]; do
    case "$1" in
      --from) shift; from="${1:-}";;
      *) [ -z "$node" ] && node="$1" || die "unknown upgrade-v38 option $1";;
    esac
    shift
  done
  [ -n "$node" ] && [ -n "$from" ] || die 'usage: upgrade-v38 NODE_ID --from /path/to/v38-folder'
  from="$(cd "$from" 2>/dev/null && pwd)" || die "source folder does not exist: $from"
  [ "$from" != "$ROOT" ] || die 'upgrade-v38 source and destination folders must differ'

  for candidate in "$from/cluster/inventory.json" "$from/taskforge-cluster-topology.json"; do
    if [ -s "$candidate" ] && python3 "$MANAGER" --inventory "$candidate" validate >/dev/null 2>&1; then
      topology="$candidate"; break
    fi
  done
  [ -n "$topology" ] || die 'no valid v38 cluster topology found (cluster/inventory.json or taskforge-cluster-topology.json)'

  info "importing v38 topology from $topology"
  cp -a "$topology" "$INVENTORY"; chmod 600 "$INVENTORY"; fix_owner "$INVENTORY"
  cp -a "$topology" "$ROOT/taskforge-cluster-topology.json"; chmod 600 "$ROOT/taskforge-cluster-topology.json"; fix_owner "$ROOT/taskforge-cluster-topology.json"

  cmd_migrate_local "$node" --from "$from"
  prepare_runtime
  printf 'upgraded-v38 %s node=%s source=%s\n' "$(date -u +%FT%TZ)" "$node" "$from" > "$EASY_RUNTIME/upgraded-v38-to-v39"
  chmod 600 "$EASY_RUNTIME/upgraded-v38-to-v39"; fix_owner "$EASY_RUNTIME/upgraded-v38-to-v39"
  echo 'v38 -> v39 local upgrade complete. Existing PostgreSQL/MinIO data volumes were preserved.'
  echo 'UFW rules were staged safely and UFW was enabled automatically unless TASKFORGE_FIREWALL_AUTO_ENABLE=0.'
  echo 'No PostgreSQL basebackup, promotion, or Docker data-volume recreation was performed.'
}

cmd_finalize_v39(){
  need_root; need_env; prepare_inventory
  local assume=0 id ip pg_port minio_port
  while [ $# -gt 0 ]; do
    case "$1" in --yes) assume=1;; *) die 'usage: finalize-v39 --yes';; esac
    shift
  done
  [ "$assume" -eq 1 ] || die 'add --yes after all nodes have been upgraded/repaired to v39 and are reachable'

  # Fail fast with an explicit endpoint error before invoking mc. This makes a
  # broken WireGuard proxy obvious instead of surfacing as an opaque alias error.
  while IFS='|' read -r id ip pg_port minio_port; do
    [ -n "$id" ] || continue
    timeout 4 bash -c "</dev/tcp/$ip/$pg_port" 2>/dev/null || die "PostgreSQL cluster endpoint $id unreachable at $ip:$pg_port; run v39 upgrade/repair on $id"
    curl -fsS --connect-timeout 3 --max-time 5 "http://$ip:$minio_port/minio/health/ready" >/dev/null 2>&1 || die "MinIO cluster endpoint $id unreachable/not-ready at $ip:$minio_port; run v39 upgrade/repair on $id"
    info "cluster endpoints $id reachable (PostgreSQL $ip:$pg_port, MinIO $ip:$minio_port)"
  done < <(python3 - "$INVENTORY" <<'PY_ENDPOINTS'
import json,sys
for n in json.load(open(sys.argv[1],encoding='utf-8'))['nodes']:
    print(f"{n['id']}|{n['wireguard']['ip']}|{n['postgres'].get('cluster_port',5432)}|{n['minio'].get('cluster_port',9000)}")
PY_ENDPOINTS
)

  sync_role_marker_from_postgres
  cmd_sync_minio --yes
  prepare_runtime
  printf 'finalized-v39 %s node=%s\n' "$(date -u +%FT%TZ)" "$(node_id)" > "$EASY_RUNTIME/finalized-v39"
  chmod 600 "$EASY_RUNTIME/finalized-v39"; fix_owner "$EASY_RUNTIME/finalized-v39"
  echo 'v39 cluster finalization complete. Cluster endpoints, both MinIO buckets, and every directed replication path were verified.'
}

cmd_finalize_v40(){
  cmd_finalize_v39 "$@"
}

# Compatibility aliases. v40 keeps the proven MinIO finalization implementation.
cmd_finalize_v38(){
  warn 'finalize-v38 is a compatibility alias in v39; running finalize-v39'
  cmd_finalize_v39 "$@"
}
cmd_finalize_v37(){
  warn 'finalize-v37 is deprecated; running finalize-v39'
  cmd_finalize_v39 "$@"
}
cmd_finalize_v36(){
  warn 'finalize-v36 is deprecated; running finalize-v39'
  cmd_finalize_v39 "$@"
}
cmd_finalize_v35(){
  warn 'finalize-v35 is deprecated; running finalize-v39'
  cmd_finalize_v39 "$@"
}

cmd_promote(){
  need_root; auto_host_deps; need_env; prepare_inventory
  local node="$(node_id)" old="$(primary_id)" confirm=0 force=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --old-primary) shift; old="${1:-}";;
      --confirm-old-primary-off) confirm=1;;
      --force) force=1;;
      *) die "usage: promote [--old-primary A] --confirm-old-primary-off [--force]";;
    esac
    shift
  done
  [ "$confirm" -eq 1 ] || die 'promotion requires --confirm-old-primary-off'
  [ "$node" != "$old" ] || die "$node is already the named primary"
  old_ip="$(node_field "$old" 'd["wireguard"]["ip"]')"; old_port="$(node_field "$old" 'd["postgres"].get("cluster_port",5432)')"
  if timeout 3 bash -c "</dev/tcp/$old_ip/$old_port" 2>/dev/null; then
    [ "$force" -eq 1 ] || die "old primary $old still answers at $old_ip:$old_port; stop it first"
  fi
  pg="$(pg_container)"; docker inspect "$pg" >/dev/null 2>&1 || die 'local PostgreSQL is not running'
  recovery="$(docker exec "$pg" sh -lc 'psql -X -U "$POSTGRES_USER" -d postgres -Atc "select pg_is_in_recovery()"')"
  if [ "$recovery" = t ]; then
    docker exec "$pg" sh -lc 'psql -X -U "$POSTGRES_USER" -d postgres -v ON_ERROR_STOP=1 -c "select pg_promote(true,60)"'
  fi
  recovery="$(docker exec "$pg" sh -lc 'psql -X -U "$POSTGRES_USER" -d postgres -Atc "select pg_is_in_recovery()"')"
  [ "$recovery" = f ] || die 'PostgreSQL promotion failed'
  role_set primary
  compose up -d
  apply_proxies "$node"
  echo "Node $node promoted and full TaskForge stack started. Rejoin $old before any failback."
}

cmd_status(){
  need_env; prepare_inventory
  local node mode pg health recovery minio c running row client state sync lag receiver sender_host sender_port slot replay_gap
  node="$(node_id 2>/dev/null || echo unset)"
  mode="$(python3 - "$INVENTORY" <<'PY'
import json,sys
print(json.load(open(sys.argv[1],encoding='utf-8')).get('mode','replica'))
PY
)"
  echo "TASKFORGE CLUSTER node=$node mode=$mode role=$(role_effective) preferred=$(primary_id)"
  echo "Docker: $(docker --version 2>/dev/null || echo unavailable)"
  echo "WireGuard: $(systemctl is-active wg-quick@wg-taskforge 2>/dev/null || echo inactive)"
  wg show wg-taskforge latest-handshakes 2>/dev/null | awk '{printf "  peer %s handshake_epoch=%s\n",$1,$2}' || true
  if [ "$node" != unset ]; then
    local cluster_ip cluster_pg_port cluster_minio_port pg_listener=missing minio_listener=missing
    cluster_ip="$(node_field "$node" 'd["wireguard"]["ip"]')"
    cluster_pg_port="$(node_field "$node" 'd["postgres"].get("cluster_port",5432)')"
    cluster_minio_port="$(node_field "$node" 'd["minio"].get("cluster_port",9000)')"
    proxy_listener_present "$cluster_ip" "$cluster_pg_port" && pg_listener=listening
    proxy_listener_present "$cluster_ip" "$cluster_minio_port" && minio_listener=listening
    echo "Cluster endpoints: postgres=$cluster_ip:$cluster_pg_port($pg_listener) minio=$cluster_ip:$cluster_minio_port($minio_listener)"
  fi

  pg="$(pg_container)"
  if docker inspect "$pg" >/dev/null 2>&1; then
    health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$pg" 2>/dev/null || echo unknown)"
    recovery="$(pg_scalar "$pg" 'select pg_is_in_recovery()' 2>/dev/null || echo '?')"
    if [ "$recovery" = f ]; then
      echo "PostgreSQL: PRIMARY health=$health"
      while IFS='|' read -r client state sync lag; do
        [ -n "$client" ] || continue
        echo "  replica $client state=$state sync=$sync lag=$lag"
      done < <(pg_fields "$pg" 'select client_addr,state,sync_state,pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(),replay_lsn)) from pg_stat_replication order by client_addr' 2>/dev/null || true)
    elif [ "$recovery" = t ]; then
      row="$(pg_fields "$pg" 'select status,sender_host,sender_port,slot_name from pg_stat_wal_receiver limit 1' 2>/dev/null || true)"
      IFS='|' read -r receiver sender_host sender_port slot <<<"$row"
      replay_gap="$(pg_scalar "$pg" 'select coalesce(pg_wal_lsn_diff(pg_last_wal_receive_lsn(),pg_last_wal_replay_lsn()),0)::bigint' 2>/dev/null || echo '?')"
      echo "PostgreSQL: STANDBY health=$health receiver=${receiver:-none} from=${sender_host:-?}:${sender_port:-?} slot=${slot:-?} replay_gap_bytes=$replay_gap"
    else
      echo "PostgreSQL: UNKNOWN health=$health"
    fi
  else
    echo 'PostgreSQL: not started'
  fi

  minio="$(minio_container)"
  if docker inspect "$minio" >/dev/null 2>&1; then
    echo "MinIO: $(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}} image={{.Config.Image}}' "$minio")"
  else
    echo 'MinIO: not started'
  fi
  for svc in rabbitmq redis; do
    c="$(container "$svc")"
    if docker inspect "$c" >/dev/null 2>&1; then
      echo "$svc: $(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$c")"
    else
      echo "$svc: not started"
    fi
  done
  running="$(compose ps -q 2>/dev/null | wc -l | tr -d ' ')"
  echo "Compose containers: $running"
  if [ "$node" != unset ] && systemctl is-active --quiet taskforge-cluster.service 2>/dev/null; then
    local agent_json agent_profile agent_mode agent_ready agent_hot_ready agent_images_ready agent_images_total agent_prepared agent_update
    agent_json="$(curl -fsS --connect-timeout 2 --max-time 4 "http://$cluster_ip:$(node_field "$node" 'd.get("health_port",9187)')/ha/telemetry" 2>/dev/null || true)"
    if [ -n "$agent_json" ]; then
      IFS='|' read -r agent_profile agent_mode agent_ready agent_hot_ready agent_images_ready agent_images_total agent_prepared agent_update < <(python3 -c 'import json,sys; d=json.load(sys.stdin); print("%s|%s|%s|%s|%s|%s|%s|%s"%(d.get("node",{}).get("app_profile","?"),d.get("ha",{}).get("app_mode","?"),str(d.get("ha",{}).get("traffic_ready",False)).lower(),str(d.get("ha",{}).get("hot_start_ready",False)).lower(),d.get("docker",{}).get("images_ready",0),d.get("docker",{}).get("assigned_app_count",0),d.get("docker",{}).get("prepared_app_count",0),d.get("update",{}).get("status","?")))' <<<"$agent_json")
      echo "Node agent: active profile=$agent_profile app_mode=$agent_mode traffic_ready=$agent_ready hot_ready=$agent_hot_ready images=$agent_images_ready/$agent_images_total prepared=$agent_prepared/$agent_images_total update=$agent_update"
    else
      echo 'Node agent: active but telemetry unavailable'
    fi
  elif [ -f "$CLUSTER_DIR/cluster.json" ]; then
    echo 'Node agent: inactive'
  fi
}

cmd_doctor(){
  # doctor is diagnostic: one failed probe must not abort the remaining checks.
  set +e
  local issues=0 node peers_expected peers_actual pg health recovery receiver minio c row sender_host sender_port slot replay_gap expected_primary_ip replica_rows replica_count expected_replicas state sync lag actual_role role_marker local_ip local_pg_port local_minio_port sensitive_ports exposed_ports port
  ok(){ echo "OK   $*"; }
  bad(){ echo "FAIL $*" >&2; issues=$((issues+1)); }
  warn_d(){ echo "WARN $*" >&2; }

  [ -s "$ENV_FILE" ] && ok '.env exists' || bad '.env missing'
  if [ -s "$INVENTORY" ] && python3 "$MANAGER" --inventory "$INVENTORY" validate >/dev/null 2>&1; then ok 'inventory valid'; else bad 'inventory invalid/missing'; fi
  docker info >/dev/null 2>&1 && ok 'Docker access' || bad 'Docker access (reconnect SSH after docker-group change)'
  [ -d "$ROOT/.runtime" ] && [ -w "$ROOT/.runtime" ] && ok '.runtime writable' || bad '.runtime not writable'
  systemctl is-active --quiet wg-quick@wg-taskforge 2>/dev/null && ok 'WireGuard active' || bad 'WireGuard inactive'

  if [ -s "$RUNTIME/node-id" ]; then
    node="$(node_id 2>/dev/null || true)"
    if [ -n "$node" ]; then
      peers_expected="$(python3 - "$INVENTORY" "$node" <<'PY' 2>/dev/null
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
print(sum(n['id']!=sys.argv[2] for n in cfg['nodes']))
PY
)"
      peers_actual="$(wg show wg-taskforge peers 2>/dev/null | wc -w | tr -d ' ')"
      [ "$peers_actual" = "$peers_expected" ] && ok "WireGuard peers=$peers_actual" || bad "WireGuard peers expected=${peers_expected:-?} actual=${peers_actual:-?}"
    else
      bad 'node id unreadable'
    fi
  else
    bad 'node id not set'
  fi

  sensitive_ports='5432 9000 9001 9187 8008 2379 2380 5672 6379 18080 18090'
  if [ -n "${node:-}" ]; then
    sensitive_ports="$(node_field "$node" '[d["postgres"].get("local_port",5432),d["postgres"].get("cluster_port",5432),d["postgres"].get("patroni_rest_port",8008),d["minio"].get("local_port",9000),d["minio"].get("cluster_port",9000),d["minio"].get("console_port",9001),d.get("health_port",9187),d.get("rabbitmq_port",5672),d.get("redis_port",6379),d.get("browser_cluster_port",18080),d.get("image_analyzer_cluster_port",18090)]' 2>/dev/null | tr -d '[],' || true) 2379 2380"
  fi
  exposed_ports=''
  for port in $sensitive_ports; do
    [[ "$port" =~ ^[0-9]+$ ]] || continue
    if ss -H -lnt 2>/dev/null | awk '{print $4}' | grep -Eq "^(0\.0\.0\.0|\[::\]):${port}$"; then
      exposed_ports="${exposed_ports}${exposed_ports:+,}${port}"
    fi
  done
  if [ -n "$exposed_ports" ]; then
    bad "sensitive port(s) globally exposed: $exposed_ports"
  else
    ok 'sensitive data/control ports not globally exposed'
  fi
  if [ -n "${node:-}" ]; then
    local_ip="$(node_field "$node" 'd["wireguard"]["ip"]' 2>/dev/null)"
    local_pg_port="$(node_field "$node" 'd["postgres"].get("cluster_port",5432)' 2>/dev/null)"
    local_minio_port="$(node_field "$node" 'd["minio"].get("cluster_port",9000)' 2>/dev/null)"
    if proxy_listener_present "$local_ip" "$local_pg_port"; then
      ok "PostgreSQL cluster endpoint listening $local_ip:$local_pg_port"
    else
      bad "PostgreSQL cluster endpoint NOT listening $local_ip:$local_pg_port"
    fi
    if proxy_listener_present "$local_ip" "$local_minio_port"; then
      ok "MinIO cluster endpoint listening $local_ip:$local_minio_port"
      if curl -fsS --connect-timeout 2 --max-time 4 "http://$local_ip:$local_minio_port/minio/health/ready" >/dev/null 2>&1; then
        ok "MinIO cluster endpoint ready $local_ip:$local_minio_port"
      else
        bad "MinIO cluster endpoint not forwarding/ready $local_ip:$local_minio_port"
      fi
    else
      bad "MinIO cluster endpoint NOT listening $local_ip:$local_minio_port"
    fi
  fi
  if ! command -v ufw >/dev/null 2>&1; then
    bad 'UFW not installed'
  elif firewall_is_active; then
    ok 'UFW active'
  else
    bad 'UFW inactive (v40 repair/apply enables it safely after staging SSH rules)'
  fi

  if [ -f "$CLUSTER_DIR/cluster.json" ]; then
    if systemctl is-active --quiet taskforge-cluster.service 2>/dev/null; then
      ok 'Node Agent active'
      if [ -n "${local_ip:-}" ]; then
        local agent_doctor_json agent_doctor_row agent_profile agent_mode agent_traffic agent_hot agent_watchtower agent_blockers
        agent_doctor_json="$(curl -fsS --connect-timeout 2 --max-time 4 "http://$local_ip:$(node_field "$node" 'd.get("health_port",9187)' 2>/dev/null)/ha/telemetry" 2>/dev/null)"
        if [ -n "$agent_doctor_json" ]; then
          ok 'Node Agent telemetry reachable'
          agent_doctor_row="$(python3 -c 'import json,sys; d=json.load(sys.stdin); print("|".join([str(d.get("node",{}).get("app_profile","none")),str(d.get("ha",{}).get("app_mode","off")),str(bool(d.get("ha",{}).get("traffic_ready",False))).lower(),str(bool(d.get("ha",{}).get("hot_start_ready",False))).lower(),str(bool(d.get("update",{}).get("watchtower_running",False))).lower(),",".join(d.get("ha",{}).get("hot_start_blockers",[]) or [])]))' <<<"$agent_doctor_json")"
          IFS='|' read -r agent_profile agent_mode agent_traffic agent_hot agent_watchtower agent_blockers <<<"$agent_doctor_row"
          if [ "$agent_profile" != none ]; then
            [ "$agent_watchtower" = true ] && ok 'Watchtower active for application profile' || bad 'Watchtower inactive for application profile'
            case "$agent_mode" in
              primary|primary-existing|assist)
                [ "$agent_traffic" = true ] && ok "application traffic ready mode=$agent_mode" || bad "application not traffic-ready mode=$agent_mode"
                ;;
              warm-standby)
                [ "$agent_hot" = true ] && ok 'application hot standby ready' || bad "application hot standby NOT ready blockers=${agent_blockers:-unknown}"
                ;;
            esac
          fi
        else
          bad 'Node Agent telemetry unreachable'
        fi
      else
        bad 'Node Agent telemetry unreachable'
      fi
    else
      bad 'Node Agent inactive'
    fi
  fi

  pg="$(pg_container)"
  if docker inspect "$pg" >/dev/null 2>&1; then
    health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$pg" 2>/dev/null)"
    [ "$health" = healthy ] && ok 'PostgreSQL healthy' || bad "PostgreSQL health=${health:-unknown}"
    recovery="$(pg_scalar "$pg" 'select pg_is_in_recovery()' 2>/dev/null)"
    if [ "$recovery" = t ]; then
      row="$(pg_fields "$pg" 'select status,sender_host,sender_port,slot_name from pg_stat_wal_receiver limit 1' 2>/dev/null)"
      IFS='|' read -r receiver sender_host sender_port slot <<<"$row"
      if [ "$receiver" = streaming ]; then
        ok "standby streaming from=${sender_host:-?}:${sender_port:-?} slot=${slot:-?}"
      else
        bad "standby receiver=${receiver:-none}"
      fi
      replay_gap="$(pg_scalar "$pg" 'select coalesce(pg_wal_lsn_diff(pg_last_wal_receive_lsn(),pg_last_wal_replay_lsn()),0)::bigint' 2>/dev/null)"
      [ -n "$replay_gap" ] && ok "standby replay gap=${replay_gap} bytes" || warn_d 'standby replay gap unavailable'
      expected_primary_ip="$(node_field "$(current_primary_id)" 'd["wireguard"]["ip"]' 2>/dev/null)"
      [ -z "$expected_primary_ip" ] || [ "$sender_host" = "$expected_primary_ip" ] && ok "standby source=$sender_host" || bad "standby source expected=$expected_primary_ip actual=${sender_host:-none}"
    elif [ "$recovery" = f ]; then
      ok 'primary writable'
      replica_rows="$(pg_fields "$pg" 'select client_addr,state,sync_state,pg_wal_lsn_diff(pg_current_wal_lsn(),replay_lsn)::bigint from pg_stat_replication order by client_addr' 2>/dev/null)"
      replica_count=0
      while IFS='|' read -r client state sync lag; do
        [ -n "$client" ] || continue
        replica_count=$((replica_count+1))
        [ "$state" = streaming ] && ok "replica $client streaming" || bad "replica $client state=${state:-none}"
        [ "$sync" = async ] && ok "replica $client async" || warn_d "replica $client sync_state=${sync:-unknown}"
        [ -n "$lag" ] && ok "replica $client lag=${lag} bytes" || warn_d "replica $client lag unavailable"
      done <<<"$replica_rows"
      expected_replicas="$(python3 - "$INVENTORY" <<'PY' 2>/dev/null
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
print(max(0,len(cfg['nodes'])-1))
PY
)"
      [ "$replica_count" -ge "${expected_replicas:-0}" ] && ok "primary replicas=$replica_count" || bad "primary replicas expected=${expected_replicas:-?} actual=$replica_count"
    else
      bad 'PostgreSQL role unknown'
    fi
    actual_role="$(pg_actual_role "$pg" 2>/dev/null || echo unknown)"
    role_marker="$(role_get)"
    if [ -f "$RUNTIME/enabled" ]; then
      # Patroni/etcd are authoritative after quorum mode is enabled; the legacy
      # replica-mode role marker is intentionally not rewritten on every failover.
      ok "PostgreSQL role=$actual_role (Patroni authoritative)"
    elif [ "$actual_role" = "$role_marker" ]; then
      ok "role marker=$role_marker"
    elif [ "$role_marker" = unknown ] || [ "$role_marker" = unconfigured ]; then
      warn_d "role marker=$role_marker actual=$actual_role (repair/adopt will fix marker)"
    else
      bad "role marker mismatch marker=$role_marker actual=$actual_role"
    fi
  else
    warn_d 'PostgreSQL not started'
  fi

  minio="$(minio_container)"
  if docker inspect "$minio" >/dev/null 2>&1; then
    health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$minio" 2>/dev/null)"
    [ "$health" = healthy ] && ok 'MinIO healthy' || bad "MinIO health=${health:-unknown}"
    docker logs --tail 30 "$minio" 2>&1 | grep -q x86-64-v2 && bad 'MinIO CPU image mismatch' || ok 'MinIO CPU image compatible'
  else
    warn_d 'MinIO not started'
  fi

  for svc in rabbitmq redis; do
    c="$(container "$svc")"
    if docker inspect "$c" >/dev/null 2>&1; then
      health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$c" 2>/dev/null)"
      [ "$health" = healthy ] && ok "$svc healthy" || bad "$svc health=${health:-unknown}"
    else
      warn_d "$svc not started"
    fi
  done

  if [ -x "$RUNTIME/bin/mc" ] && [ -n "${node:-}" ]; then
    local mc="$RUNTIME/bin/mc" user pass bucket data_bucket tmp_mc id ip port rules target_label
    local -a doctor_buckets
    user="$(env_get MINIO_ROOT_USER)"; user="${user:-taskforge}"
    pass="$(env_get MINIO_ROOT_PASSWORD)"
    data_bucket="$(env_get S3_BUCKET)"; data_bucket="${data_bucket:-taskforge-files}"
    doctor_buckets=("$data_bucket" taskforge-cluster-state)
    tmp_mc="$(mktemp -d)"
    MC_CONFIG_DIR="$tmp_mc" "$mc" alias set local "http://127.0.0.1:$(node_field "$node" 'd["minio"]["local_port"]')" "$user" "$pass" >/dev/null 2>&1
    for bucket in "${doctor_buckets[@]}"; do
      if MC_CONFIG_DIR="$tmp_mc" "$mc" version info "local/$bucket" 2>/dev/null | grep -qi enabled; then
        ok "MinIO bucket versioning enabled bucket=$bucket"
      else
        bad "MinIO bucket versioning not enabled bucket=$bucket"
        continue
      fi
      rules="$(MC_CONFIG_DIR="$tmp_mc" "$mc" replicate ls "local/$bucket" 2>/dev/null || true)"
      while IFS='|' read -r id ip port; do
        [ "$id" = "$node" ] && continue
        target_label="$ip:$port/$bucket"
        if minio_rule_present_in_text "$rules" "$target_label"; then
          ok "MinIO replication rule $node->$id bucket=$bucket"
        else
          bad "MinIO replication rule missing $node->$id bucket=$bucket"
        fi
      done < <(python3 - "$INVENTORY" <<'PY_DOCTOR_NODES' 2>/dev/null
import json,sys
for n in json.load(open(sys.argv[1],encoding='utf-8'))['nodes']:
    print(f"{n['id']}|{n['wireguard']['ip']}|{n['minio'].get('cluster_port',9000)}")
PY_DOCTOR_NODES
)
    done
    rm -rf "$tmp_mc"
  else
    warn_d 'MinIO replication-rule check unavailable until node adoption installs mc'
  fi

  set -e
  if [ "$issues" -eq 0 ]; then
    echo 'TaskForge cluster doctor: healthy'
    return 0
  fi
  echo "TaskForge cluster doctor: $issues issue(s)" >&2
  return 1
}

cmd_repair(){
  need_root; need_env; prepare_inventory
  [ ! -f "$RUNTIME/enabled" ] || die 'advanced quorum mode is enabled; use ./cluster/ops/quorum/status.sh and Patroni recovery tools'
  local node="${1:-$(node_id)}" owner group
  owner="$(owner_user)"; group="$(owner_group)"; find "$ROOT" -type f -name '*.sh' -exec chmod +x {} +; mkdir -p "$ROOT/.runtime"; chown -R "$owner:$group" "$ROOT/.runtime"; chmod 700 "$ROOT/.runtime"; [ ! -f "$ROOT/.env" ] || { chown "$owner:$group" "$ROOT/.env"; chmod 600 "$ROOT/.env"; }; [ ! -f "$ROOT/config.json" ] || { chown "$owner:$group" "$ROOT/config.json"; chmod 600 "$ROOT/config.json"; }
  cmd_apply "$node"
  if docker inspect "$(pg_container)" >/dev/null 2>&1; then compose up -d --no-deps postgres; wait_health "$(pg_container)" 240; fi
  if docker inspect "$(minio_container)" >/dev/null 2>&1; then compose up -d --no-deps minio; wait_health "$(minio_container)" 240; fi
  apply_proxies "$node"
  sync_role_marker_from_postgres
  echo 'Safe repairs applied. Role marker reconciled. No data volume was deleted.'
}

cmd_quorum(){
  need_root; auto_host_deps; need_env; prepare_inventory
  local phase="${1:-}" node="${2:-$(node_id 2>/dev/null || true)}"; [ -n "$node" ] || die 'apply a node id first'
  case "$phase" in
    prepare)
      mgr render-quorum --output "$CLUSTER_DIR/cluster.json" >/dev/null
      python3 "$CLUSTER_DIR/clusterctl.py" --config "$CLUSTER_DIR/cluster.json" --env-file "$ENV_FILE" validate
      "$CLUSTER_DIR/ops/quorum/set-node.sh" "$node"; apply_wireguard "$node"; apply_firewall "$node"
      # Preparation must be non-disruptive. Replica-mode PostgreSQL/MinIO WG
      # proxies stay alive until the exact standalone -> Patroni handoff in
      # migrate-primary/join-node. Removing them here used to make C unreachable
      # while operators were still preparing the remaining voters.
      voter="$(python3 - "$INVENTORY" "$node" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8')); print(str(next(n for n in cfg['nodes'] if n['id']==sys.argv[2]).get('quorum_voter',False)).lower())
PY
)"
      if [ "$voter" = true ]; then
        "$CLUSTER_DIR/ops/quorum/start-dcs.sh"
        if ! "$CLUSTER_DIR/ops/quorum/dcs-status.sh" >/dev/null 2>&1; then
          warn "local etcd started; full quorum will become healthy after the other voters are prepared"
        fi
      fi
      echo "Quorum prepared on $node without interrupting existing data endpoints. Repeat on all voters."
      ;;
    enable-primary) [ "$node" = "$(primary_id)" ] || die "run on $(primary_id)"; "$CLUSTER_DIR/ops/quorum/migrate-primary.sh" --yes;;
    join) "$CLUSTER_DIR/ops/quorum/join-node.sh" --yes;;
    *) die 'usage: quorum prepare|enable-primary|join';;
  esac
}

command_name="${1:-}"
case "$command_name" in
  prepare-node|add-node|import-topology|apply|adopt|init-primary|join|migrate-local|upgrade-v34|upgrade-v35|upgrade-v36|upgrade-v38|upgrade-v39|finalize-v35|finalize-v36|finalize-v37|finalize-v38|finalize-v39|finalize-v40|sync-minio|promote|repair|quorum)
    acquire_operation_lock
    ;;
esac

case "${1:-}" in
  cpu-profile) shift; cmd_cpu_profile "$@";;
  prepare-node) shift; cmd_prepare_node "$@";;
  add-node) shift; cmd_add_node "$@";;
  import-topology) shift; cmd_import_topology "$@";;
  apply) shift; cmd_apply "$@";;
  adopt) shift; cmd_adopt "$@";;
  init-primary) shift; cmd_init_primary "$@";;
  join) shift; cmd_join "$@";;
  migrate-local) shift; cmd_migrate_local "$@";;
  upgrade-v34) shift; cmd_upgrade_v34 "$@";;
  upgrade-v35) shift; cmd_upgrade_v35 "$@";;
  upgrade-v36) shift; cmd_upgrade_v36 "$@";;
  upgrade-v38) shift; cmd_upgrade_v38 "$@";;
  upgrade-v39) shift; cmd_upgrade_v39 "$@";;
  finalize-v35) shift; cmd_finalize_v35 "$@";;
  finalize-v36) shift; cmd_finalize_v36 "$@";;
  finalize-v37) shift; cmd_finalize_v37 "$@";;
  finalize-v38) shift; cmd_finalize_v38 "$@";;
  finalize-v39) shift; cmd_finalize_v39 "$@";;
  finalize-v40) shift; cmd_finalize_v40 "$@";;
  sync-minio) shift; cmd_sync_minio "$@";;
  promote) shift; cmd_promote "$@";;
  status) shift; cmd_status "$@";;
  doctor) shift; cmd_doctor "$@";;
  repair) shift; cmd_repair "$@";;
  quorum) shift; cmd_quorum "$@";;
  *) die "unknown easy cluster command: ${1:-<empty>}";;
esac
