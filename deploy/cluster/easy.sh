#!/usr/bin/env bash
set -Eeuo pipefail

CLUSTER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -x "$CLUSTER_DIR/../compose.sh" ]; then
  ROOT="$(cd "$CLUSTER_DIR/.." && pwd)"
  COMPOSE="$ROOT/compose.sh"
  CHECK="$ROOT/check.sh"
  BACKUP="$ROOT/backup.sh"
  ENV_FILE="${TASKFORGE_ENV_FILE:-$ROOT/.env}"
else
  ROOT="$(cd "$CLUSTER_DIR/../.." && pwd)"
  COMPOSE="$ROOT/deploy/prod/compose.sh"
  CHECK="$ROOT/scripts/prod/check-prod-config.sh"
  BACKUP=""
  ENV_FILE="${TASKFORGE_ENV_FILE:-$ROOT/deploy/prod/.env}"
fi
MANAGER="$CLUSTER_DIR/manager.py"
ENSURE_HOST="$CLUSTER_DIR/ensure-host.sh"
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
  local node="$1" actual expected
  ensure_wg_key
  actual="$(cat /etc/wireguard/taskforge-public.key)"; expected="$(node_field "$node" 'd["wireguard"]["public_key"]')"
  [ "$actual" = "$expected" ] || die "WireGuard key mismatch for $node. inventory=$expected local=$actual"
  mgr render-wireguard --node "$node" --private-key-file /etc/wireguard/taskforge-private.key --output /etc/wireguard/wg-taskforge.conf >/dev/null
  chmod 600 /etc/wireguard/wg-taskforge.conf
  systemctl enable wg-quick@wg-taskforge >/dev/null
  systemctl restart wg-quick@wg-taskforge
}

apply_firewall(){
  local node="$1" local_ip local_wg_port pg_port minio_port health
  command -v ufw >/dev/null 2>&1 || { warn 'ufw not installed; firewall automation skipped'; return; }
  ufw status | head -1 | grep -q active || { warn 'ufw inactive; firewall rules not changed'; return; }
  local_ip="$(node_field "$node" 'd["wireguard"]["ip"]')"; local_wg_port="$(node_field "$node" 'd["wireguard"]["listen_port"]')"
  pg_port="$(node_field "$node" 'd["postgres"].get("cluster_port",5432)')"; minio_port="$(node_field "$node" 'd["minio"].get("cluster_port",9000)')"; health="$(node_field "$node" 'd.get("health_port",9187)')"
  while IFS='|' read -r peer public_host peer_ip; do
    [ -n "$peer" ] || continue
    public_ip="$public_host"; [[ "$public_ip" =~ ^[0-9]+(\.[0-9]+){3}$ ]] || public_ip="$(getent ahostsv4 "$public_host" | awk 'NR==1{print $1}')"
    [ -n "$public_ip" ] || die "cannot resolve $public_host"
    ufw allow from "$public_ip" to any port "$local_wg_port" proto udp comment "TaskForge WG $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$pg_port" proto tcp comment "TaskForge PostgreSQL $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$minio_port" proto tcp comment "TaskForge MinIO $peer" >/dev/null
    ufw allow in on wg-taskforge from "$peer_ip" to "$local_ip" port "$health" proto tcp comment "TaskForge health $peer" >/dev/null
  done < <(python3 - "$INVENTORY" "$node" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8'))
for n in cfg['nodes']:
    if n['id'] != sys.argv[2]: print(f"{n['id']}|{n['public_host']}|{n['wireguard']['ip']}")
PY
)
}

install_proxy(){
  local name bind_ip cluster_port local_port unit
  name="$1"
  bind_ip="$2"
  cluster_port="$3"
  local_port="$4"
  unit="taskforge-${name}-wg-proxy.service"
  if ss -H -ltn | awk '{print $4}' | grep -Fqx "$bind_ip:$cluster_port"; then
    systemctl disable --now "$unit" >/dev/null 2>&1 || true; rm -f "/etc/systemd/system/$unit"
    info "$name already listens directly on $bind_ip:$cluster_port"
    return
  fi
  cat > "/etc/systemd/system/$unit" <<EOF
[Unit]
Description=TaskForge $name WireGuard TCP proxy
After=network-online.target wg-quick@wg-taskforge.service docker.service
Wants=network-online.target wg-quick@wg-taskforge.service
[Service]
ExecStart=/usr/bin/socat TCP-LISTEN:${cluster_port},bind=${bind_ip},reuseaddr,fork TCP:127.0.0.1:${local_port}
Restart=always
RestartSec=2
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload; systemctl enable --now "$unit" >/dev/null
  info "$name proxy $bind_ip:$cluster_port -> 127.0.0.1:$local_port"
}

apply_proxies(){
  local node="$1" ip pg_local pg_cluster mi_local mi_cluster
  ip="$(node_field "$node" 'd["wireguard"]["ip"]')"
  pg_local="$(node_field "$node" 'd["postgres"]["local_port"]')"
  pg_cluster="$(node_field "$node" 'd["postgres"].get("cluster_port",5432)')"
  mi_local="$(node_field "$node" 'd["minio"]["local_port"]')"
  mi_cluster="$(node_field "$node" 'd["minio"].get("cluster_port",9000)')"
  install_proxy postgres "$ip" "$pg_cluster" "$pg_local"
  install_proxy minio "$ip" "$mi_cluster" "$mi_local"
}

cmd_apply(){
  need_root; auto_host_deps; need_env; prepare_inventory; need wg; need socat; need docker
  local node="${1:-}"; [ -n "$node" ] || die 'usage: apply NODE_ID'
  mgr set-node "$node" >/dev/null; mgr render-local --node "$node" >/dev/null; cmd_cpu_profile --write >/dev/null
  apply_wireguard "$node"; apply_firewall "$node"; apply_proxies "$node"
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
  if [ -x "$BACKUP" ]; then sudo -u "$(owner_user)" "$BACKUP" --metadata; docker inspect "$(pg_container)" >/dev/null 2>&1 && sudo -u "$(owner_user)" "$BACKUP" --database || true; fi
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
  [ -x "$RUNTIME/bin/mc" ] || "$CLUSTER_DIR/install-mc.sh" >/dev/null
  if [ "$(id -u)" -eq 0 ]; then chown "$(owner_pair)" "$RUNTIME/bin/mc"; fi
  chmod 700 "$RUNTIME/bin/mc"
}
minio_prepare_local(){
  local node="$1" user pass bucket port mc
  install_mc; mc="$RUNTIME/bin/mc"; export MC_CONFIG_DIR="$RUNTIME/mc-easy"; mkdir -p "$MC_CONFIG_DIR"; chmod 700 "$MC_CONFIG_DIR"; [ "$(id -u)" -ne 0 ] || fix_owner "$MC_CONFIG_DIR"
  user="$(env_get MINIO_ROOT_USER)"; user="${user:-taskforge}"; pass="$(env_get MINIO_ROOT_PASSWORD)"; bucket="$(env_get S3_BUCKET)"; bucket="${bucket:-taskforge-files}"; port="$(node_field "$node" 'd["minio"]["local_port"]')"
  "$mc" alias set local "http://127.0.0.1:$port" "$user" "$pass" >/dev/null
  "$mc" mb --ignore-existing --with-versioning "local/$bucket" >/dev/null || true; "$mc" version enable "local/$bucket" >/dev/null
}

cmd_sync_minio(){
  need_env; prepare_inventory
  local assume=0 test_rules=1; while [ $# -gt 0 ]; do case "$1" in --yes) assume=1;; --no-test) test_rules=0;; *) die 'usage: sync-minio --yes [--no-test]';; esac; shift; done
  [ "$assume" -eq 1 ] || die 'add --yes after all configured MinIO nodes are reachable'
  install_mc; local mc="$RUNTIME/bin/mc" user pass bucket primary local_id target stamp key tmp current target_label out
  export MC_CONFIG_DIR="$RUNTIME/mc-easy"; mkdir -p "$MC_CONFIG_DIR"; chmod 700 "$MC_CONFIG_DIR"; [ "$(id -u)" -ne 0 ] || fix_owner "$MC_CONFIG_DIR"
  user="$(env_get MINIO_ROOT_USER)"; user="${user:-taskforge}"; pass="$(env_get MINIO_ROOT_PASSWORD)"; bucket="$(env_get S3_BUCKET)"; bucket="${bucket:-taskforge-files}"; primary="$(primary_id)"
  mapfile -t rows < <(python3 - "$INVENTORY" <<'PY'
import json,sys
for n in json.load(open(sys.argv[1],encoding='utf-8'))['nodes']:
    print(f"{n['id']}|{n['wireguard']['ip']}|{n['minio'].get('cluster_port',9000)}")
PY
)
  ids=()
  for row in "${rows[@]}"; do IFS='|' read -r id ip port <<<"$row"; ids+=("$id"); "$mc" alias set "tf-$id" "http://$ip:$port" "$user" "$pass" >/dev/null; "$mc" admin info "tf-$id" >/dev/null || die "MinIO $id unreachable at $ip:$port"; "$mc" mb --ignore-existing --with-versioning "tf-$id/$bucket" >/dev/null || true; "$mc" version enable "tf-$id/$bucket" >/dev/null; done
  for id in "${ids[@]}"; do [ "$id" = "$primary" ] && continue; if ! "$mc" ls --recursive "tf-$id/$bucket" 2>/dev/null | grep -q .; then "$mc" mirror --overwrite "tf-$primary/$bucket" "tf-$id/$bucket" >/dev/null; fi; done
  for source_row in "${rows[@]}"; do
    IFS='|' read -r source _ _ <<<"$source_row"; current="$("$mc" replicate ls "tf-$source/$bucket" 2>&1 || true)"
    for target_row in "${rows[@]}"; do
      IFS='|' read -r target target_ip target_port <<<"$target_row"; [ "$source" = "$target" ] && continue; target_label="$target_ip:$target_port/$bucket"
      if ! grep -Fq "Remote Bucket: $target_label" <<<"$current"; then
        set +e; out="$("$mc" replicate add "tf-$source/$bucket" --remote-bucket "tf-$target/$bucket" --replicate 'delete,delete-marker,existing-objects' 2>&1)"; rc=$?; set -e
        if [ "$rc" -ne 0 ] && ! grep -Eqi 'already|exists|duplicate|same rule' <<<"$out"; then echo "$out" >&2; die "cannot add MinIO rule $source -> $target"; fi
        info "MinIO rule $source -> $target configured"; current="$("$mc" replicate ls "tf-$source/$bucket" 2>&1 || true)"
      fi
    done
  done
  if [ "$test_rules" -eq 1 ] && [ "${#ids[@]}" -ge 2 ]; then
    local_id="$(node_id 2>/dev/null || echo "$primary")"; target=''; for id in "${ids[@]}"; do [ "$id" = "$local_id" ] || { target="$id"; break; }; done
    tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
    for pair in "$local_id:$target" "$target:$local_id"; do
      source_id="${pair%%:*}"; target_id="${pair##*:}"; stamp="$(date -u +%Y%m%d%H%M%S)-$$-$source_id"; key="__cluster_probe__/$stamp.txt"; echo "$stamp" > "$tmp"
      "$mc" cp "$tmp" "tf-$source_id/$bucket/$key" >/dev/null; ok=0
      for _ in $(seq 1 30); do "$mc" stat "tf-$target_id/$bucket/$key" >/dev/null 2>&1 && { ok=1; break; }; sleep 1; done
      [ "$ok" -eq 1 ] || die "MinIO probe $source_id -> $target_id failed"
      "$mc" rm "tf-$source_id/$bucket/$key" >/dev/null; info "MinIO probe $source_id -> $target_id passed"
    done
  fi
  prepare_runtime; printf 'configured %s nodes=%s\n' "$(date -u +%FT%TZ)" "${ids[*]}" > "$EASY_RUNTIME/minio-synced"; chmod 600 "$EASY_RUNTIME/minio-synced"; [ "$(id -u)" -ne 0 ] || fix_owner "$EASY_RUNTIME/minio-synced"
  echo 'MinIO N-way asynchronous replication is ready.'
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
  compose up -d --no-deps minio rabbitmq redis; wait_health "$(minio_container)" 240; wait_health "$(container rabbitmq)" 180; wait_health "$(container redis)" 120
  cmd_sync_minio --yes
  role_set standby; printf 'joined %s primary=%s slot=%s\n' "$(date -u +%FT%TZ)" "$primary" "$slot" > "$EASY_RUNTIME/joined"; chmod 600 "$EASY_RUNTIME/joined"; fix_owner "$EASY_RUNTIME/joined"
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
  chmod 700 "$ROOT/.runtime" "$ROOT/.runtime/code-analyzer-keys"
  chmod 600 "$ROOT/.runtime/code-analyzer-keys/code-analyzer-private.pem"
  chmod 644 "$ROOT/.runtime/code-analyzer-keys/code-analyzer-public.pem"

  sudo -u "$owner" "$CHECK"
  info "local migration complete; adopting existing node $node"
  cmd_adopt "$node"
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
  echo "TASKFORGE CLUSTER node=$node mode=$mode role=$(role_get) preferred=$(primary_id)"
  echo "Docker: $(docker --version 2>/dev/null || echo unavailable)"
  echo "WireGuard: $(systemctl is-active wg-quick@wg-taskforge 2>/dev/null || echo inactive)"
  wg show wg-taskforge latest-handshakes 2>/dev/null | awk '{printf "  peer %s handshake_epoch=%s\n",$1,$2}' || true

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
}

cmd_doctor(){
  # doctor is diagnostic: one failed probe must not abort the remaining checks.
  set +e
  local issues=0 node peers_expected peers_actual pg health recovery receiver minio c row sender_host sender_port slot replay_gap expected_primary_ip replica_rows replica_count expected_replicas state sync lag
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

  if ss -H -lnt 2>/dev/null | awk '{print $4}' | grep -Eq '^(0\.0\.0\.0|\[::\]):(5432|9000)$'; then
    bad 'data port globally exposed'
  else
    ok 'data ports not globally exposed'
  fi
  systemctl is-active --quiet ufw 2>/dev/null && ok 'UFW active' || warn_d 'UFW service not active/unknown'

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
      expected_primary_ip="$(node_field "$(primary_id)" 'd["wireguard"]["ip"]' 2>/dev/null)"
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
    local mc="$RUNTIME/bin/mc" user pass bucket tmp_mc id ip port rules target_label
    user="$(env_get MINIO_ROOT_USER)"; user="${user:-taskforge}"
    pass="$(env_get MINIO_ROOT_PASSWORD)"
    bucket="$(env_get S3_BUCKET)"; bucket="${bucket:-taskforge-files}"
    tmp_mc="$(mktemp -d)"
    MC_CONFIG_DIR="$tmp_mc" "$mc" alias set local "http://127.0.0.1:$(node_field "$node" 'd["minio"]["local_port"]')" "$user" "$pass" >/dev/null 2>&1
    if MC_CONFIG_DIR="$tmp_mc" "$mc" version info "local/$bucket" 2>/dev/null | grep -qi enabled; then ok 'MinIO bucket versioning enabled'; else bad 'MinIO bucket versioning not enabled'; fi
    rules="$(MC_CONFIG_DIR="$tmp_mc" "$mc" replicate ls "local/$bucket" 2>/dev/null)"
    while IFS='|' read -r id ip port; do
      [ "$id" = "$node" ] && continue
      target_label="$ip:$port/$bucket"
      grep -Fq "Remote Bucket: $target_label" <<<"$rules" && ok "MinIO replication rule $node->$id" || bad "MinIO replication rule missing $node->$id"
    done < <(python3 - "$INVENTORY" <<'PY' 2>/dev/null
import json,sys
for n in json.load(open(sys.argv[1],encoding='utf-8'))['nodes']:
    print(f"{n['id']}|{n['wireguard']['ip']}|{n['minio'].get('cluster_port',9000)}")
PY
)
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
  [ ! -f "$RUNTIME/enabled" ] || die 'advanced quorum mode is enabled; use ./cluster/status.sh and Patroni recovery tools'
  local node="${1:-$(node_id)}" owner group
  owner="$(owner_user)"; group="$(owner_group)"; find "$ROOT" -type f -name '*.sh' -exec chmod +x {} +; mkdir -p "$ROOT/.runtime"; chown -R "$owner:$group" "$ROOT/.runtime"; chmod 700 "$ROOT/.runtime"; [ ! -f "$ROOT/.env" ] || { chown "$owner:$group" "$ROOT/.env"; chmod 600 "$ROOT/.env"; }; [ ! -f "$ROOT/config.json" ] || { chown "$owner:$group" "$ROOT/config.json"; chmod 600 "$ROOT/config.json"; }
  cmd_apply "$node"
  if docker inspect "$(pg_container)" >/dev/null 2>&1; then compose up -d --no-deps postgres; wait_health "$(pg_container)" 240; fi
  if docker inspect "$(minio_container)" >/dev/null 2>&1; then compose up -d --no-deps minio; wait_health "$(minio_container)" 240; fi
  apply_proxies "$node"
  echo 'Safe repairs applied. No data volume was deleted.'
}

cmd_quorum(){
  need_root; auto_host_deps; need_env; prepare_inventory
  local phase="${1:-}" node="${2:-$(node_id 2>/dev/null || true)}"; [ -n "$node" ] || die 'apply a node id first'
  case "$phase" in
    prepare)
      mgr render-quorum --output "$CLUSTER_DIR/cluster.json" >/dev/null
      python3 "$CLUSTER_DIR/clusterctl.py" --config "$CLUSTER_DIR/cluster.json" --env-file "$ENV_FILE" validate
      "$CLUSTER_DIR/set-node.sh" "$node"; apply_wireguard "$node"
      # Patroni/MinIO bind directly to WireGuard addresses in quorum mode.
      # Remove replica-mode TCP proxies before those listeners start.
      systemctl disable --now taskforge-postgres-wg-proxy.service taskforge-minio-wg-proxy.service >/dev/null 2>&1 || true
      voter="$(python3 - "$INVENTORY" "$node" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1],encoding='utf-8')); print(str(next(n for n in cfg['nodes'] if n['id']==sys.argv[2]).get('quorum_voter',False)).lower())
PY
)"; [ "$voter" != true ] || { "$CLUSTER_DIR/start-dcs.sh"; "$CLUSTER_DIR/wait-dcs.sh" 300; }
      echo "Quorum prepared on $node. Repeat on all voters."
      ;;
    enable-primary) [ "$node" = "$(primary_id)" ] || die "run on $(primary_id)"; "$CLUSTER_DIR/migrate-primary.sh" --yes;;
    join) "$CLUSTER_DIR/join-node.sh" --yes;;
    *) die 'usage: quorum prepare|enable-primary|join';;
  esac
}

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
  sync-minio) shift; cmd_sync_minio "$@";;
  promote) shift; cmd_promote "$@";;
  status) shift; cmd_status "$@";;
  doctor) shift; cmd_doctor "$@";;
  repair) shift; cmd_repair "$@";;
  quorum) shift; cmd_quorum "$@";;
  *) die "unknown easy cluster command: ${1:-<empty>}";;
esac
