#!/usr/bin/env bash
set -Eeuo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run with sudo" >&2; exit 2; }
local_ip="${1:-}"
peer_pub="${2:-}"
peer_endpoint="${3:-}"
peer_ip="${4:-}"
listen_port="${5:-51820}"
[ -n "$local_ip" ] && [ -n "$peer_pub" ] && [ -n "$peer_endpoint" ] && [ -n "$peer_ip" ] || {
  echo "usage: sudo ./ha/wireguard-apply.sh 10.80.0.1 PEER_PUBLIC_KEY 203.0.113.20:51820 10.80.0.2 [51820]" >&2
  exit 2
}
key=/etc/wireguard/taskforge-private.key
[ -s "$key" ] || { echo "run ./ha/wireguard-generate.sh first" >&2; exit 2; }
private="$(cat "$key")"
conf=/etc/wireguard/wg-taskforge.conf
umask 077
cat > "$conf" <<EOF
[Interface]
Address = ${local_ip}/32
ListenPort = ${listen_port}
PrivateKey = ${private}

[Peer]
PublicKey = ${peer_pub}
AllowedIPs = ${peer_ip}/32
Endpoint = ${peer_endpoint}
PersistentKeepalive = 25
EOF
chmod 600 "$conf"
systemctl enable wg-quick@wg-taskforge.service
systemctl restart wg-quick@wg-taskforge.service
wg show wg-taskforge
echo "Test: ping -c 3 $peer_ip"
