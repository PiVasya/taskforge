#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"

cluster_need_root
cluster_need_env
cluster_need_config
cluster_need_command docker
cluster_need_command gzip
clusterctl validate
cluster_render >/dev/null

assume=0
reset_cluster_data=0
for arg in "$@"; do
  case "$arg" in
    --yes) assume=1 ;;
    --reset-cluster-data) reset_cluster_data=1 ;;
    *) cluster_die "usage: sudo ./cluster/ops/quorum/migrate-primary.sh --yes [--reset-cluster-data]" ;;
  esac
done
[ "$assume" -eq 1 ] || cluster_die "add --yes after reading cluster/QUICK_START_RU.md"

node="$(cluster_node_id)"
preferred="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["preferred_primary"])' "$TASKFORGE_CLUSTER_CONFIG")"
[ "$node" = "$preferred" ] || cluster_die "initial migration must run on preferred_primary=$preferred, current node=$node"
rollback_marker="$TASKFORGE_CLUSTER_RUNTIME/bootstrap-rolled-back"
if [ -f "$rollback_marker" ]; then
  if [ "$reset_cluster_data" -ne 1 ]; then
    cluster_die "a previous bootstrap was rolled back. Retry only with: sudo ./cluster/ops/quorum/migrate-primary.sh --yes --reset-cluster-data"
  fi
  cluster_info "clearing the abandoned Patroni PostgreSQL volume before a fresh bootstrap retry"
  systemctl stop taskforge-cluster.service 2>/dev/null || true
  cluster_compose_force rm -s -f postgres >/dev/null 2>&1 || true
  cluster_volume="$(cluster_volume_name postgres-cluster-data)"
  if docker volume inspect "$cluster_volume" >/dev/null 2>&1; then
    docker volume rm "$cluster_volume" >/dev/null
  fi
  rm -f "$rollback_marker" \
    "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-complete" \
    "$TASKFORGE_CLUSTER_RUNTIME/join-complete" \
    "$TASKFORGE_CLUSTER_RUNTIME/enabled" \
    "$TASKFORGE_READINESS_RUNTIME/traffic-ready"
  cluster_render >/dev/null
fi
[ ! -f "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-complete" ] || cluster_die "cluster bootstrap is already marked complete"
[ ! -f "$TASKFORGE_CLUSTER_RUNTIME/enabled" ] || cluster_die "cluster mode is already enabled; use cluster/ops/quorum/status.sh instead"

standalone_frozen=0
cluster_activated=0
on_exit(){
  rc=$?
  if [ "$rc" -ne 0 ] && [ "$standalone_frozen" -eq 1 ] && [ "$cluster_activated" -eq 0 ]; then
    cluster_warn "migration failed before cluster activation; restarting the preserved standalone stack"
    cluster_compose up -d || true
  fi
  return "$rc"
}
trap on_exit EXIT

wg_ip="$(cluster_local_field 'd["wireguard"]["ip"]')"
cluster_has_wg_ip "$wg_ip"
"$CLUSTER_DIR/ops/quorum/wait-dcs.sh" 240 >/dev/null

pg_user="$(cluster_read_env POSTGRES_USER)"; pg_user="${pg_user:-taskforge}"
stamp="$(date -u +%Y%m%d-%H%M%S)"
backup_dir="$TASKFORGE_CLUSTER_RUNTIME/bootstrap-backups/$stamp"
mkdir -p "$backup_dir"
chmod 700 "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-backups" "$backup_dir"

if ! cluster_compose ps --status running postgres 2>/dev/null | grep -q postgres; then
  cluster_die "standalone PostgreSQL is not running; start the current TaskForge stack before migration"
fi

cluster_info "freezing write-capable TaskForge services for a consistent final backup"
mapfile -t app_services < <(cluster_compose config --services | grep -Ev '^(postgres|rabbitmq|redis|minio|watchtower|certbot|etcd)$' || true)
if [ "${#app_services[@]}" -gt 0 ]; then
  cluster_compose stop --timeout 30 "${app_services[@]}"
fi
standalone_frozen=1

cluster_info "creating final logical database backup before Patroni migration"
cluster_compose exec -T postgres psql -U "$pg_user" -d postgres -Atc \
  "select datname from pg_database where datallowconn and not datistemplate order by datname" \
  > "$backup_dir/databases-before.txt"
cluster_compose exec -T postgres pg_dumpall -U "$pg_user" | gzip -1 > "$backup_dir/postgres-all.sql.gz"
[ -s "$backup_dir/postgres-all.sql.gz" ] || cluster_die "database backup is empty"
cp -p "$TASKFORGE_ENV_FILE" "$backup_dir/env"
cp -p "$TASKFORGE_CLUSTER_CONFIG" "$backup_dir/cluster.json"
cluster_compose ps > "$backup_dir/compose-before.txt" 2>&1 || true
chmod 600 "$backup_dir/env" "$backup_dir/postgres-all.sql.gz"

"$CLUSTER_DIR/ops/quorum/migrate-shared-volumes.sh"

cluster_info "stopping the standalone TaskForge stack; named volumes are preserved"
cluster_compose down
# The standalone backends are now stopped, so the replica-mode socat endpoints
# can be retired without creating a preparation-time outage. Quorum-mode
# PostgreSQL/MinIO will immediately claim the same WireGuard addresses.
cluster_retire_replica_data_proxies
cluster_enable_marker
cluster_activated=1
cluster_render >/dev/null

cluster_info "starting Patroni on the empty dedicated cluster volume"
cluster_compose up -d postgres
if ! "$CLUSTER_DIR/ops/quorum/wait-patroni.sh" primary 300; then
  cluster_compose logs --tail 200 postgres || true
  cluster_die "Patroni did not bootstrap the preferred primary. Run cluster/ops/quorum/rollback-bootstrap.sh --yes to restore the old standalone stack."
fi

cluster_info "restoring TaskForge databases into the new Patroni primary"
set +e
gzip -dc "$backup_dir/postgres-all.sql.gz" \
  | cluster_compose exec -T postgres psql -X -U "$pg_user" -d postgres -v ON_ERROR_STOP=0 \
  > "$backup_dir/restore.log" 2>&1
restore_rc=$?
set -e
# pg_dumpall can report harmless duplicate bootstrap-role/database messages in
# the fresh Patroni cluster. Database presence and application health are the
# authoritative checks below; the complete log is preserved for review.
if [ "$restore_rc" -ne 0 ]; then
  cluster_warn "psql restore returned $restore_rc; reviewing restore errors and database list"
fi
python3 - "$backup_dir/restore.log" "$pg_user" <<'PY_RESTORE_ERRORS'
from pathlib import Path
import re, sys

log = Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace").splitlines()
pg_user = re.escape(sys.argv[2])
allowed = [
    re.compile(rf'ERROR:\s+role "{pg_user}" already exists'),
    re.compile(r'ERROR:\s+database "postgres" already exists'),
]
unexpected = [line for line in log if "ERROR:" in line and not any(p.search(line) for p in allowed)]
if unexpected:
    print("unexpected PostgreSQL restore errors:", file=sys.stderr)
    for line in unexpected[:40]:
        print("  " + line, file=sys.stderr)
    raise SystemExit(1)
PY_RESTORE_ERRORS
cluster_compose exec -T postgres psql -U "$pg_user" -d postgres -Atc \
  "select datname from pg_database where datallowconn and not datistemplate order by datname" \
  > "$backup_dir/databases-after.txt"
missing="$(comm -23 "$backup_dir/databases-before.txt" "$backup_dir/databases-after.txt" || true)"
if [ -n "$missing" ]; then
  echo "$missing" >&2
  cluster_die "databases above were not restored. Inspect $backup_dir/restore.log and run cluster/ops/quorum/rollback-bootstrap.sh --yes if necessary."
fi

cluster_info "starting local object storage and publishing shared DataProtection keys"
cluster_compose up -d minio rabbitmq redis watchtower
for _ in $(seq 1 90); do
  if cluster_compose ps --status running minio 2>/dev/null | grep -q minio; then break; fi
  sleep 2
done
"$CLUSTER_DIR/ops/quorum/shared-state.sh" push || cluster_warn "shared state upload will be retried by the cluster agent"

cluster_info "installing the role controller; only the Patroni primary starts TaskForge applications"
"$CLUSTER_DIR/ops/quorum/install-service.sh"
health_port="$(cluster_local_field 'd.get("health_port",9187)')"
if ! cluster_wait_http "http://$wg_ip:$health_port/ha/traffic-ready" 200 900; then
  journalctl -u taskforge-cluster.service --no-pager -n 200 || true
  cluster_die "primary database is restored, but the application stack did not become ready. Do not delete either PostgreSQL volume."
fi

printf '%s\n' "completed $(date -u +%FT%TZ) backup=$backup_dir" > "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-complete"
chmod 600 "$TASKFORGE_CLUSTER_RUNTIME/bootstrap-complete"
trap - EXIT
cluster_info "primary migration complete"
echo "Backup and restore log: $backup_dir"
echo "Next: run cluster/ops/quorum/join-node.sh --yes on every remaining node."
