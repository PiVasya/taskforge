#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/minio-replication.sh"
cluster_need_env
cluster_need_config
assume=0
mode=rules
seed=""
new_node=""
while [ $# -gt 0 ]; do
  case "$1" in
    --yes) assume=1 ;;
    --initial) mode=initial ;;
    --add-node) shift; mode=add; new_node="${1:-}" ;;
    --seed-from) shift; seed="${1:-}" ;;
    *) cluster_die "unknown option: $1" ;;
  esac
  shift
done
[ "$assume" -eq 1 ] || cluster_die "add --yes after reviewing cluster/README.md"
clusterctl validate
if [ -z "$seed" ]; then
  seed="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["preferred_primary"])' "$TASKFORGE_CLUSTER_CONFIG")"
fi
mcbin="$TASKFORGE_CLUSTER_RUNTIME/bin/mc"
[ -x "$mcbin" ] || "$CLUSTER_DIR/ops/host/install-mc.sh" >/dev/null
export MC_CONFIG_DIR="$TASKFORGE_CLUSTER_RUNTIME/mc-config"
mkdir -p "$MC_CONFIG_DIR"; chmod 700 "$MC_CONFIG_DIR"
user="$(cluster_read_env MINIO_ROOT_USER)"; user="${user:-taskforge}"
pass="$(cluster_read_env MINIO_ROOT_PASSWORD)"
bucket="$(cluster_read_env S3_BUCKET)"; bucket="${bucket:-taskforge-files}"
state_bucket="taskforge-cluster-state"
[ -n "$pass" ] || cluster_die "MINIO_ROOT_PASSWORD is empty"
mapfile -t nodes < <(python3 - "$TASKFORGE_CLUSTER_CONFIG" <<'PY'
import json,sys
for n in json.load(open(sys.argv[1],encoding='utf-8'))['nodes']:
 print(f"{n['id']}|{n['wireguard']['ip']}|{n['minio']['api_port']}|{int(n['priority'])}")
PY
)
ids=()
for row in "${nodes[@]}"; do
  IFS='|' read -r id ip port priority <<<"$row"
  ids+=("$id")
  "$mcbin" alias set "tf-$id" "http://$ip:$port" "$user" "$pass" >/dev/null
  "$mcbin" admin info "tf-$id" >/dev/null || cluster_die "MinIO node $id is unreachable at $ip:$port"
  "$mcbin" mb --ignore-existing "tf-$id/$bucket" >/dev/null
  "$mcbin" mb --ignore-existing "tf-$id/$state_bucket" >/dev/null
  "$mcbin" version enable "tf-$id/$bucket" >/dev/null
  "$mcbin" version enable "tf-$id/$state_bucket" >/dev/null
done
contains_id(){ local wanted="$1" x; for x in "${ids[@]}"; do [ "$x" = "$wanted" ] && return 0; done; return 1; }
contains_id "$seed" || cluster_die "seed node does not exist: $seed"
if [ "$mode" = initial ]; then
  for id in "${ids[@]}"; do
    [ "$id" = "$seed" ] && continue
    if "$mcbin" ls --recursive "tf-$id/$bucket" | grep -q .; then
      cluster_die "initial target $id is not empty; do not merge independent object histories automatically"
    fi
    echo "Seeding $bucket: $seed -> $id"
    "$mcbin" mirror --overwrite "tf-$seed/$bucket" "tf-$id/$bucket"
  done
elif [ "$mode" = add ]; then
  contains_id "$new_node" || cluster_die "new node does not exist: $new_node"
  [ "$new_node" != "$seed" ] || cluster_die "new node cannot be the seed node"
  if "$mcbin" ls --recursive "tf-$new_node/$bucket" | grep -q .; then
    cluster_die "new node $new_node is not empty; refusing automatic merge"
  fi
  echo "Seeding new node $new_node from $seed"
  "$mcbin" mirror --overwrite "tf-$seed/$bucket" "tf-$new_node/$bucket"
fi
replicate_features='delete,delete-marker,existing-objects,metadata-sync'
add_rule(){
  local source="$1" target="$2" target_ip="$3" target_port="$4" target_bucket="$5" target_priority="$6"
  local expected result
  expected="$target_ip:$target_port/$target_bucket"
  if ! result="$(minio_add_rule_verified "$mcbin" "tf-$source/$target_bucket" "tf-$target/$target_bucket" "$expected" "$target_priority" "$replicate_features")"; then
    cluster_die "cannot configure verified MinIO replication rule $source -> $target for $target_bucket (priority=$target_priority)"
  fi
  if [ "$result" = created ]; then
    cluster_info "MinIO rule $source -> $target bucket=$target_bucket priority=$target_priority configured"
  fi
}
for source_row in "${nodes[@]}"; do
  IFS='|' read -r source source_ip source_port source_priority <<<"$source_row"
  for target_row in "${nodes[@]}"; do
    IFS='|' read -r target target_ip target_port target_priority <<<"$target_row"
    [ "$source" = "$target" ] && continue
    add_rule "$source" "$target" "$target_ip" "$target_port" "$bucket" "$target_priority"
    add_rule "$source" "$target" "$target_ip" "$target_port" "$state_bucket" "$target_priority"
  done
done

mkdir -p "$TASKFORGE_CLUSTER_RUNTIME"
printf '%s\n' "configured $(date -u +%FT%TZ) nodes=${ids[*]}" > "$TASKFORGE_CLUSTER_RUNTIME/minio-replication-configured"
chmod 600 "$TASKFORGE_CLUSTER_RUNTIME/minio-replication-configured"
echo "MinIO N-node asynchronous bucket replication configured."
