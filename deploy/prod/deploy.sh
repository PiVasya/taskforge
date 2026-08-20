#!/usr/bin/env bash
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

scripts/prod/prepare-env.sh
scripts/prod/check-prod-config.sh
source deploy/ha/common.sh
if ha_bool "$(ha_read_env HA_ENABLED)"; then
  if systemctl is-active --quiet taskforge-ha.service 2>/dev/null; then
    ./deploy/prod/compose.sh pull
    ha_agent apply-update
    ./deploy/ha/status.sh
  else
    echo "HA_ENABLED=true but taskforge-ha is not installed yet." >&2
    echo "Initialize with deploy/ha/setup-primary.sh or deploy/ha/setup-standby.sh." >&2
    exit 2
  fi
else
  ./deploy/prod/compose.sh up-logs --pull missing
fi

echo ""
echo "TaskForge production deploy command complete."
