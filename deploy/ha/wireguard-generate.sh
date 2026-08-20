#!/usr/bin/env bash
set -Eeuo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run with sudo" >&2; exit 2; }
command -v wg >/dev/null 2>&1 || {
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update && apt-get install -y wireguard
  else
    echo "wireguard-tools is required" >&2; exit 2
  fi
}
install -d -m 700 /etc/wireguard
key=/etc/wireguard/taskforge-private.key
if [ ! -s "$key" ]; then
  umask 077
  wg genkey > "$key"
fi
chmod 600 "$key"
echo "Private key: $key"
printf 'Public key:  '
wg pubkey < "$key"
