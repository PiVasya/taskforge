#!/usr/bin/env bash
set -Eeuo pipefail

# Called only AFTER this node has fenced the previous primary and successfully
# promoted itself. Implement this with your hosting provider's independent
# control-plane API if you want the old node to power on automatically and
# rejoin. Exit 0 only when the provider accepted/confirmed the power-on request.
#
# Available environment variables:
#   HA_TARGET_NODE_ID   A or B
#   HA_TARGET_WG_IP     target private WireGuard address
#   HA_TARGET_PUBLIC_IP target public IP when configured
#   HA_LOCAL_NODE_ID    node that is now primary
#
# Provider credentials must live outside the repository, root-readable only.

echo "provider recovery hook is not configured for node ${HA_TARGET_NODE_ID:-?}" >&2
exit 1
