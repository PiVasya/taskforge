#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_env
cluster_need_config
action="${1:-}"
case "$action" in push|pull) ;; *) cluster_die "usage: ./cluster/shared-state.sh push|pull";; esac
cluster_runtime_prepare
cluster_render >/dev/null
mcbin="$TASKFORGE_CLUSTER_RUNTIME/bin/mc"
[ -x "$mcbin" ] || "$CLUSTER_DIR/install-mc.sh" >/dev/null
export MC_CONFIG_DIR="$TASKFORGE_CLUSTER_RUNTIME/mc-config"
mkdir -p "$MC_CONFIG_DIR"; chmod 700 "$MC_CONFIG_DIR"
user="$(cluster_read_env MINIO_ROOT_USER)"; user="${user:-taskforge}"
pass="$(cluster_read_env MINIO_ROOT_PASSWORD)"
node_json="$(clusterctl node-info)"
ip="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["wireguard"]["ip"])')"
port="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["minio"]["api_port"])')"
node="$(cluster_node_id)"
"$mcbin" alias set tf-local "http://$ip:$port" "$user" "$pass" >/dev/null
"$mcbin" mb --ignore-existing tf-local/taskforge-cluster-state >/dev/null
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
object="tf-local/taskforge-cluster-state/shared/data-protection.tar.gz"
hash_object="tf-local/taskforge-cluster-state/shared/data-protection.sha256"
local_hash_file="$TASKFORGE_CLUSTER_RUNTIME/shared-state-last-pushed.sha256"

state_hash(){
  python3 - "$TASKFORGE_CLUSTER_RUNTIME/shared" <<'PY_HASH'
from pathlib import Path
import hashlib,sys
root=Path(sys.argv[1])
h=hashlib.sha256()
for path in sorted(p for p in root.rglob('*') if p.is_file()):
    rel=path.relative_to(root).as_posix().encode('utf-8')
    h.update(len(rel).to_bytes(4,'big')); h.update(rel)
    data=path.read_bytes()
    h.update(len(data).to_bytes(8,'big')); h.update(data)
print(h.hexdigest())
PY_HASH
}

if [ "$action" = push ]; then
  current_hash="$(state_hash)"
  previous_hash="$(cat "$local_hash_file" 2>/dev/null || true)"
  if [ "$current_hash" = "$previous_hash" ] \
      && "$mcbin" stat "$object" >/dev/null 2>&1 \
      && "$mcbin" stat "$hash_object" >/dev/null 2>&1; then
    echo "shared DataProtection state is unchanged"
    exit 0
  fi
  tar -C "$TASKFORGE_CLUSTER_RUNTIME/shared" -czf "$work/data-protection.tar.gz" \
    content-data-protection quiz-data-protection
  printf '%s\n' "$current_hash" > "$work/data-protection.sha256"
  "$mcbin" cp --quiet "$work/data-protection.tar.gz" "$object"
  "$mcbin" cp --quiet "$work/data-protection.sha256" "$hash_object"
  printf '%s\n' "node=$node timestamp=$(date -u +%FT%TZ) sha256=$current_hash" > "$work/meta.txt"
  "$mcbin" cp --quiet "$work/meta.txt" tf-local/taskforge-cluster-state/shared/meta.txt
  printf '%s\n' "$current_hash" > "$local_hash_file"
  chmod 600 "$local_hash_file"
  echo "shared DataProtection state uploaded"
else
  if ! "$mcbin" stat "$object" >/dev/null 2>&1; then
    echo "shared state does not exist yet; nothing to restore"
    exit 0
  fi
  "$mcbin" cp --quiet "$object" "$work/data-protection.tar.gz"
  tar -C "$TASKFORGE_CLUSTER_RUNTIME/shared" -xzf "$work/data-protection.tar.gz"
  restored_hash="$(state_hash)"
  if "$mcbin" stat "$hash_object" >/dev/null 2>&1; then
    "$mcbin" cp --quiet "$hash_object" "$work/data-protection.sha256"
    expected_hash="$(tr -d '\r\n' < "$work/data-protection.sha256")"
    [ "$restored_hash" = "$expected_hash" ] || cluster_die "shared-state checksum mismatch after restore"
  fi
  printf '%s\n' "$restored_hash" > "$local_hash_file"
  chmod 600 "$local_hash_file"
  echo "shared DataProtection state restored"
fi
