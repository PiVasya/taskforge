#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_env

check_only=0
if [ "${1:-}" = "--check-only" ]; then
  check_only=1
  shift
fi
timeout_seconds="${1:-$(ha_read_env HA_MINIO_DRAIN_TIMEOUT_SECONDS)}"
timeout_seconds="${timeout_seconds:-300}"
[[ "$timeout_seconds" =~ ^[0-9]+$ ]] || ha_die "MinIO drain timeout must be an integer number of seconds"
[ "$timeout_seconds" -ge 30 ] || ha_die "MinIO drain timeout must be at least 30 seconds"

mcbin="$TASKFORGE_ROOT/.runtime/ha/bin/mc"
[ -x "$mcbin" ] || "$HA_DIR/install-mc.sh" >/dev/null
export MC_CONFIG_DIR="$TASKFORGE_ROOT/.runtime/ha/mc-config"
mkdir -p "$MC_CONFIG_DIR"
chmod 700 "$MC_CONFIG_DIR"

user="$(ha_read_env MINIO_ROOT_USER)"; user="${user:-taskforge}"
pass="$(ha_read_env MINIO_ROOT_PASSWORD)"
bucket="$(ha_read_env S3_BUCKET)"; bucket="${bucket:-taskforge-files}"
local_ip="$(ha_read_env HA_WG_IP)"
port="$(ha_read_env MINIO_PORT)"; port="${port:-9000}"
[ -n "$pass" ] && [ -n "$local_ip" ] || ha_die "MinIO/HA variables are incomplete"

# Use only the local source bucket. The replication target is already part of
# the server-side replication rule; the backlog command reports objects and
# delete markers that have not reached that target yet.
"$mcbin" alias set tf-local "http://$local_ip:$port" "$user" "$pass" >/dev/null

rules_file="$(mktemp "$TASKFORGE_ROOT/.runtime/ha/minio-rules.XXXXXX")"
trap 'rm -f "$rules_file" "${rules_file}.err"' EXIT
if ! "$mcbin" --json replicate ls --status enabled "tf-local/$bucket" >"$rules_file" 2>"${rules_file}.err"; then
  detail="$(tr '\n' ' ' <"${rules_file}.err" | sed 's/[[:space:]]\+/ /g' | cut -c1-500)"
  ha_die "cannot list MinIO replication rules: ${detail:-unknown error}"
fi
python3 - "$rules_file" <<'PYRULES' || ha_die "no enabled MinIO replication rule exists for tf-local/$bucket"
import json, sys
count = 0
for line in open(sys.argv[1], encoding='utf-8'):
    line = line.strip()
    if not line:
        continue
    try:
        doc = json.loads(line)
    except json.JSONDecodeError:
        continue
    rule = doc.get('rule') if isinstance(doc, dict) else None
    if isinstance(rule, dict) and str(rule.get('Status', '')).lower() == 'enabled':
        count += 1
raise SystemExit(0 if count else 1)
PYRULES
rm -f "$rules_file" "${rules_file}.err"
trap - EXIT
if [ "$check_only" -eq 1 ]; then
  echo "MinIO replication rule is configured."
  exit 0
fi

deadline=$((SECONDS + timeout_seconds))
attempt=0
while true; do
  attempt=$((attempt + 1))
  first_line_file="$(mktemp "$TASKFORGE_ROOT/.runtime/ha/minio-backlog.XXXXXX")"
  err_file="${first_line_file}.err"
  set +e
  "$mcbin" replicate backlog "tf-local/$bucket" 2>"$err_file" | head -n 1 >"$first_line_file"
  mc_rc=${PIPESTATUS[0]}
  set -e

  if [ ! -s "$first_line_file" ]; then
    if [ "$mc_rc" -eq 0 ]; then
      rm -f "$first_line_file" "$err_file"
      echo "MinIO replication backlog is empty."
      exit 0
    fi
    detail="$(tr '\n' ' ' <"$err_file" | sed 's/[[:space:]]\+/ /g' | cut -c1-500)"
    rm -f "$first_line_file" "$err_file"
    ha_die "cannot inspect MinIO replication backlog (mc exit $mc_rc): ${detail:-unknown error}"
  fi

  first_line="$(tr '\n' ' ' <"$first_line_file" | cut -c1-500)"
  rm -f "$first_line_file" "$err_file"

  if [ "$SECONDS" -ge "$deadline" ]; then
    ha_die "MinIO replication backlog did not drain within ${timeout_seconds}s; first pending entry: $first_line"
  fi

  if [ "$attempt" -eq 1 ] || [ $((attempt % 6)) -eq 0 ]; then
    echo "Waiting for MinIO replication backlog to drain... first pending entry: $first_line"
  fi
  sleep 5
done
