#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_env
cluster_need_config
timeout_seconds="${1:-300}"
[[ "$timeout_seconds" =~ ^[0-9]+$ ]] || cluster_die "timeout must be an integer"
cluster_render >/dev/null
mcbin="$TASKFORGE_CLUSTER_RUNTIME/bin/mc"
[ -x "$mcbin" ] || "$CLUSTER_DIR/install-mc.sh" >/dev/null
export MC_CONFIG_DIR="$TASKFORGE_CLUSTER_RUNTIME/mc-config"
mkdir -p "$MC_CONFIG_DIR"; chmod 700 "$MC_CONFIG_DIR"
user="$(cluster_read_env MINIO_ROOT_USER)"; user="${user:-taskforge}"
pass="$(cluster_read_env MINIO_ROOT_PASSWORD)"
bucket="$(cluster_read_env S3_BUCKET)"; bucket="${bucket:-taskforge-files}"
[ -n "$pass" ] || cluster_die "MINIO_ROOT_PASSWORD is empty"
mapfile -t nodes < <(python3 - "$TASKFORGE_CLUSTER_CONFIG" <<'PY'
import json,sys
for n in json.load(open(sys.argv[1],encoding='utf-8'))['nodes']:
    print(f"{n['id']}|{n['wireguard']['ip']}|{n['minio']['api_port']}")
PY
)
for row in "${nodes[@]}"; do
  IFS='|' read -r id ip port <<<"$row"
  "$mcbin" alias set "tf-$id" "http://$ip:$port" "$user" "$pass" >/dev/null
 done
deadline=$((SECONDS + timeout_seconds))
for row in "${nodes[@]}"; do
  IFS='|' read -r id _ _ <<<"$row"
  for target_bucket in "$bucket" taskforge-cluster-state; do
    while true; do
      tmp="$(mktemp)"; err="${tmp}.err"
      set +e
      "$mcbin" replicate backlog "tf-$id/$target_bucket" >"$tmp" 2>"$err"
      rc=$?
      set -e
      if [ "$rc" -eq 0 ] && [ ! -s "$tmp" ]; then
        rm -f "$tmp" "$err"
        break
      fi
      if [ "$SECONDS" -ge "$deadline" ]; then
        detail="$(head -n 2 "$tmp" 2>/dev/null || head -n 2 "$err" 2>/dev/null || true)"
        rm -f "$tmp" "$err"
        cluster_die "MinIO backlog did not drain on node $id for $target_bucket: $detail"
      fi
      rm -f "$tmp" "$err"
      sleep 5
    done
  done
done
echo "MinIO replication backlog is empty on all configured nodes."
