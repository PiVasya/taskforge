#!/usr/bin/env bash
set -Eeuo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run with sudo/root" >&2; exit 2; }
pub="${1:-}"
source_ip="${2:-}"
[ -n "$pub" ] && [ -n "$source_ip" ] || { echo "usage: sudo ./ha/ssh-authorize-peer.sh 'ssh-ed25519 AAAA...' 10.80.0.1" >&2; exit 2; }
case "$pub" in ssh-ed25519\ *|sk-ssh-ed25519\ *) ;; *) echo "unsupported public key" >&2; exit 2;; esac
install -d -m 700 /root/.ssh
touch /root/.ssh/authorized_keys
chmod 600 /root/.ssh/authorized_keys
line="from=\"$source_ip\",restrict $pub"
grep -Fqx "$line" /root/.ssh/authorized_keys || printf '%s\n' "$line" >> /root/.ssh/authorized_keys
echo "Authorized HA SSH key only from $source_ip."
