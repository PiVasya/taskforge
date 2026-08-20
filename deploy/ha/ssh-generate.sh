#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_root
key="$TASKFORGE_ROOT/.runtime/ha/ssh/id_ed25519"
mkdir -p "$(dirname "$key")"
chmod 700 "$(dirname "$key")"
if [ ! -s "$key" ]; then
  ssh-keygen -q -t ed25519 -N '' -C "taskforge-ha-$(hostname)" -f "$key"
fi
chmod 600 "$key"
echo "HA SSH public key:"
cat "$key.pub"
echo
echo "Install this key on the peer with ./ha/ssh-authorize-peer.sh '<public-key>' <THIS_NODE_WG_IP>."
