#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_root
cluster_need_config
command -v wg >/dev/null 2>&1 || {
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y wireguard-tools
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y wireguard-tools
  else
    cluster_die "install wireguard-tools first"
  fi
}
install -d -m 700 /etc/wireguard
key=/etc/wireguard/taskforge-private.key
pub=/etc/wireguard/taskforge-public.key
if [ ! -s "$key" ]; then
  umask 077
  wg genkey > "$key"
fi
wg pubkey < "$key" > "$pub"
chmod 600 "$key" "$pub"
echo "WireGuard public key for this node:"
cat "$pub"
echo
echo "Put this value into cluster.json -> this node -> wireguard.public_key."
