#!/usr/bin/env bash
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

scripts/prod/prepare-env.sh
scripts/prod/check-prod-config.sh

restart_cluster_agent(){
  if systemctl cat taskforge-cluster.service >/dev/null 2>&1; then
    if systemctl is-active --quiet taskforge-cluster.service; then
      sudo systemctl restart taskforge-cluster.service
    else
      sudo ./deploy/cluster/ops/quorum/install-service.sh
    fi
  else
    sudo ./deploy/cluster/ops/quorum/install-service.sh
  fi
}

replica_standby(){
  [ -s .runtime/cluster/node-id ] || return 1
  [ ! -f .runtime/cluster/enabled ] || return 1
  local pg recovery
  pg="$(./deploy/prod/compose.sh ps -q postgres 2>/dev/null | head -n1 || true)"
  [ -n "$pg" ] || return 2
  recovery="$(docker exec "$pg" sh -lc 'psql -X -U "$POSTGRES_USER" -d postgres -Atc "select pg_is_in_recovery()"' 2>/dev/null || true)"
  [ "$recovery" = t ] && return 0
  [ "$recovery" = f ] && return 1
  return 2
}

if [ -f .runtime/cluster/enabled ]; then
  # Patroni/Node Agent own application start/stop in quorum mode. A manual
  # deploy may refresh local images, but must never `compose up` a standby.
  ./deploy/prod/compose.sh pull
  restart_cluster_agent
  ./deploy/prod/cluster.sh status
  echo
  echo "Cluster-mode deploy complete; Node Agent reconciles the local FULL/LITE role."
  exit 0
fi

if [ -s .runtime/cluster/node-id ]; then
  set +e
  replica_standby
  role_rc=$?
  set -e
  if [ "$role_rc" -eq 0 ]; then
    # Safe v40 pre-quorum standby maintenance: keep FULL/LITE images current and
    # let the Node Agent prepare STOPPED containers. Never start application APIs.
    ./deploy/prod/compose.sh pull
    restart_cluster_agent
    ./deploy/prod/cluster.sh status
    echo
    echo "Replica standby deploy complete; application containers remain prepared/stopped."
    exit 0
  elif [ "$role_rc" -eq 2 ]; then
    echo "Cannot determine PostgreSQL role on this assigned cluster node; refusing to start application services." >&2
    echo "Use: ./deploy/prod/cluster.sh status && ./deploy/prod/cluster.sh doctor" >&2
    exit 2
  fi
fi

# Standalone / current replica primary. This retains the traditional production
# deployment behavior; Watchtower handles subsequent rolling updates.
./deploy/prod/compose.sh up-logs --pull missing
if [ -s .runtime/cluster/node-id ] && systemctl cat taskforge-cluster.service >/dev/null 2>&1; then
  restart_cluster_agent
fi

echo
echo "TaskForge production deploy command complete."
