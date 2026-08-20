#!/usr/bin/env bash
set -Eeuo pipefail
# Configure HA_FENCE_SCRIPT to point at your own provider-specific copy of this
# file. The script runs on the surviving node before promotion.
#
# Environment supplied by agent.py:
#   HA_TARGET_NODE_ID   A or B
#   HA_TARGET_WG_IP     private WireGuard IP of the failed peer
#   HA_TARGET_PUBLIC_IP public IP of the failed peer when configured
#   HA_LOCAL_NODE_ID    surviving node
#
# REQUIRED CONTRACT:
# Exit 0 ONLY after the hosting/provider control plane has confirmed that the
# target server is powered off, fenced, or otherwise physically unable to write
# to PostgreSQL. A failed ping is NOT fencing.

echo "No hosting-provider fencing command configured for $HA_TARGET_NODE_ID" >&2
exit 1
