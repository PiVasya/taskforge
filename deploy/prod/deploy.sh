#!/usr/bin/env bash
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

scripts/prod/prepare-env.sh
scripts/prod/check-prod-config.sh

if [ -f .runtime/cluster/enabled ]; then
  ./deploy/prod/compose.sh pull
  if systemctl is-active --quiet taskforge-cluster.service 2>/dev/null; then
    sudo systemctl restart taskforge-cluster.service
    ./deploy/cluster/status.sh
  else
    echo "Cluster mode is enabled, but taskforge-cluster.service is not active." >&2
    echo "Run: sudo ./deploy/cluster/install-service.sh" >&2
    exit 2
  fi
else
  ./deploy/prod/compose.sh up-logs --pull missing
  if [ -s .runtime/cluster/node-id ]; then
    sudo ./deploy/prod/cluster.sh repair "$(cat .runtime/cluster/node-id)"
  fi
fi

echo
echo "TaskForge production deploy command complete."
