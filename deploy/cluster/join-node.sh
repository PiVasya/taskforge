#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"

cluster_need_root
cluster_need_env
cluster_need_config
cluster_need_command docker
clusterctl validate
cluster_render >/dev/null

assume=0
reset=0
for arg in "$@"; do
  case "$arg" in
    --yes) assume=1 ;;
    --reset-cluster-data) reset=1 ;;
    *) cluster_die "usage: sudo ./cluster/join-node.sh --yes [--reset-cluster-data]" ;;
  esac
done
[ "$assume" -eq 1 ] || cluster_die "add --yes after reading cluster/QUICK_START_RU.md"
node="$(cluster_node_id)"
preferred="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["preferred_primary"])' "$TASKFORGE_CLUSTER_CONFIG")"
[ "$node" != "$preferred" ] || cluster_die "use cluster/migrate-primary.sh on preferred node $preferred"
wg_ip="$(cluster_local_field 'd["wireguard"]["ip"]')"
cluster_has_wg_ip "$wg_ip"
"$CLUSTER_DIR/wait-dcs.sh" 240 >/dev/null

volume="$(cluster_volume_name postgres-cluster-data)"
if docker volume inspect "$volume" >/dev/null 2>&1; then
  if [ "$reset" -ne 1 ] && [ ! -f "$TASKFORGE_CLUSTER_RUNTIME/join-complete" ]; then
    cluster_die "cluster PostgreSQL volume $volume already exists from an incomplete attempt; rerun with --reset-cluster-data only if this node has no unique cluster data"
  fi
  if [ "$reset" -eq 1 ]; then
    systemctl stop taskforge-cluster.service 2>/dev/null || true
    cluster_compose_force rm -s -f postgres >/dev/null 2>&1 || true
    docker volume rm "$volume" >/dev/null
    rm -f "$TASKFORGE_CLUSTER_RUNTIME/join-complete"
    cluster_info "removed failed local cluster volume $volume"
  fi
fi

# Stop any standalone copy on the new node. Its old named volumes are preserved.
if ! cluster_is_enabled; then
  cluster_compose down || true
fi
cluster_enable_marker
cluster_render >/dev/null
cluster_compose up -d postgres
if ! "$CLUSTER_DIR/wait-patroni.sh" replica 1800; then
  cluster_compose logs --tail 240 postgres || true
  cluster_die "node did not join as a Patroni replica; rerun with --reset-cluster-data after correcting network/DCS settings"
fi
"$CLUSTER_DIR/install-service.sh"
health_port="$(cluster_local_field 'd.get("health_port",9187)')"
cluster_wait_http "http://$wg_ip:$health_port/ha/live" 200 180
printf '%s\n' "joined $(date -u +%FT%TZ)" > "$TASKFORGE_CLUSTER_RUNTIME/join-complete"
chmod 600 "$TASKFORGE_CLUSTER_RUNTIME/join-complete"
cluster_info "node $node joined as an asynchronous PostgreSQL replica"
echo "It stays traffic-unready until Patroni promotes it."
