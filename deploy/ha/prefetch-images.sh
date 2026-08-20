#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_env
if ! ha_bool "$(ha_read_env HA_ENABLED)"; then exit 0; fi
# The active node is already maintained by Watchtower. The standby keeps the
# same images warm without starting application services.
if [ -f "$TASKFORGE_ROOT/.runtime/ha/traffic-ready" ]; then exit 0; fi
ha_compose pull
