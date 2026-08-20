#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_env
ASSUME=0
if [ "${1:-}" = "--yes" ]; then ASSUME=1; shift; fi
[ "$ASSUME" -eq 1 ] || ha_die "run once from A after B is online: ./ha/setup-minio-replication.sh --yes"
mcbin="$TASKFORGE_ROOT/.runtime/ha/bin/mc"
[ -x "$mcbin" ] || "$HA_DIR/install-mc.sh"
export MC_CONFIG_DIR="$TASKFORGE_ROOT/.runtime/ha/mc-config"
mkdir -p "$MC_CONFIG_DIR"
chmod 700 "$MC_CONFIG_DIR"
user="$(ha_read_env MINIO_ROOT_USER)"; user="${user:-taskforge}"
pass="$(ha_read_env MINIO_ROOT_PASSWORD)"
bucket="$(ha_read_env S3_BUCKET)"; bucket="${bucket:-taskforge-files}"
local_ip="$(ha_read_env HA_WG_IP)"; peer_ip="$(ha_read_env HA_PEER_WG_IP)"
port="$(ha_read_env MINIO_PORT)"; port="${port:-9000}"
[ -n "$pass" ] && [ -n "$local_ip" ] && [ -n "$peer_ip" ] || ha_die "MinIO/HA variables are incomplete"

"$mcbin" alias set tf-local "http://$local_ip:$port" "$user" "$pass" >/dev/null
"$mcbin" alias set tf-peer "http://$peer_ip:$port" "$user" "$pass" >/dev/null
"$mcbin" mb --ignore-existing "tf-local/$bucket" >/dev/null
"$mcbin" mb --ignore-existing "tf-peer/$bucket" >/dev/null

marker="$TASKFORGE_ROOT/.runtime/ha/minio-replication-configured"
if [ -f "$marker" ]; then
  echo "Replication marker already exists: $marker"
  "$mcbin" replicate status "tf-local/$bucket" || true
  "$mcbin" replicate status "tf-peer/$bucket" || true
  exit 0
fi

if "$mcbin" ls --recursive "tf-peer/$bucket" | grep -q .; then
  ha_die "peer bucket is not empty. Refusing initial automatic merge. Verify/empty the B bucket first."
fi

echo "Seeding existing objects A -> B before versioning/replication..."
"$mcbin" mirror --overwrite "tf-local/$bucket" "tf-peer/$bucket"
"$mcbin" version enable "tf-local/$bucket"
"$mcbin" version enable "tf-peer/$bucket"

remote_local="$(python3 - "$user" "$pass" "$local_ip" "$port" "$bucket" <<'PY'
from urllib.parse import quote
import sys
u,p,h,port,b=sys.argv[1:]
print(f"http://{quote(u,safe='')}:{quote(p,safe='')}@{h}:{port}/{quote(b,safe='')}")
PY
)"
remote_peer="$(python3 - "$user" "$pass" "$peer_ip" "$port" "$bucket" <<'PY'
from urllib.parse import quote
import sys
u,p,h,port,b=sys.argv[1:]
print(f"http://{quote(u,safe='')}:{quote(p,safe='')}@{h}:{port}/{quote(b,safe='')}")
PY
)"

"$mcbin" replicate add "tf-local/$bucket" --remote-bucket "$remote_peer" --replicate "delete,delete-marker,existing-objects"
"$mcbin" replicate add "tf-peer/$bucket" --remote-bucket "$remote_local" --replicate "delete,delete-marker,existing-objects"
printf '%s\n' "configured $(date -u +%FT%TZ)" > "$marker"
chmod 600 "$marker"
echo "Two-way asynchronous MinIO bucket replication configured."
"$mcbin" replicate status "tf-local/$bucket" || true
"$mcbin" replicate status "tf-peer/$bucket" || true
