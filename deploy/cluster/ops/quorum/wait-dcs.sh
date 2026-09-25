#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_config
timeout="${1:-180}"
case "$timeout" in ''|*[!0-9]*) cluster_die "timeout must be an integer";; esac
deadline=$((SECONDS + timeout))
while [ "$SECONDS" -lt "$deadline" ]; do
  if "$CLUSTER_DIR/ops/quorum/dcs-status.sh" >/tmp/taskforge-dcs-status.$$ 2>/dev/null; then
    cat /tmp/taskforge-dcs-status.$$
    rm -f /tmp/taskforge-dcs-status.$$
    echo "etcd quorum is healthy"
    exit 0
  fi
  sleep 3
done
"$CLUSTER_DIR/ops/quorum/dcs-status.sh" || true
rm -f /tmp/taskforge-dcs-status.$$ 2>/dev/null || true
cluster_die "etcd quorum did not become healthy within ${timeout}s"
