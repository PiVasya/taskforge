#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -x "$SCRIPT_DIR/../prod/compose.sh" ]; then
  ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
  INSTALL_DOCKER="$ROOT/scripts/prod/install-ubuntu-docker.sh"
else
  ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
  INSTALL_DOCKER="$ROOT/install-ubuntu-docker.sh"
fi

QUIET=0
[ "${1:-}" != "--quiet" ] || QUIET=1
log(){ [ "$QUIET" -eq 1 ] || echo "[taskforge bootstrap] $*"; }
die(){ echo "error: $*" >&2; exit 2; }
[ "$(id -u)" -eq 0 ] || die 'run as root (normally through bash ./cluster.sh)'

OWNER="${SUDO_USER:-$(stat -c '%U' "$ROOT")}"
[ -n "$OWNER" ] || OWNER=root
[ "$OWNER" != root ] || OWNER="$(stat -c '%U' "$ROOT")"
GROUP="$(id -gn "$OWNER" 2>/dev/null || stat -c '%G' "$ROOT")"

# Package names differ slightly between Debian/Ubuntu and Fedora/RHEL.
required_commands=(curl jq openssl python3 wg socat ip ss gzip)
missing=()
for cmd in "${required_commands[@]}"; do
  command -v "$cmd" >/dev/null 2>&1 || missing+=("$cmd")
done
# At least one netcat implementation is required by some diagnostics.
command -v nc >/dev/null 2>&1 || missing+=(nc)

if [ "${#missing[@]}" -gt 0 ]; then
  log "installing missing host tools: ${missing[*]}"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y \
      ca-certificates curl jq openssl python3 wireguard-tools socat \
      iproute2 netcat-openbsd gzip
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y \
      ca-certificates curl jq openssl python3 wireguard-tools socat \
      iproute gzip nmap-ncat
  else
    die 'supported package manager not found (apt-get or dnf required)'
  fi
else
  log 'host tools already installed'
fi

if ! command -v docker >/dev/null 2>&1; then
  log 'Docker is missing; installing it automatically'
  [ -x "$INSTALL_DOCKER" ] || die "missing Docker installer: $INSTALL_DOCKER"
  "$INSTALL_DOCKER"
fi

# Docker may already be installed but stopped after a reboot.
if command -v systemctl >/dev/null 2>&1; then
  systemctl enable --now docker >/dev/null 2>&1 || true
fi

# Group membership is persistent. The current non-root SSH process still needs
# one reconnect after the very first addition, but root cluster commands work
# immediately and no manual newgrp is required by the installer.
groupadd -f docker
if id "$OWNER" >/dev/null 2>&1; then
  usermod -aG docker "$OWNER"
fi

# The archive must remain directly usable after sudo-based secret import or
# recovery operations.
find "$ROOT" -type f -name '*.sh' -exec chmod +x {} +
mkdir -p "$ROOT/.runtime"
chown -R "$OWNER:$GROUP" "$ROOT/.runtime"
chmod 700 "$ROOT/.runtime"
for file in "$ROOT/.env" "$ROOT/config.json"; do
  [ ! -f "$file" ] || { chown "$OWNER:$GROUP" "$file"; chmod 600 "$file"; }
done
if [ -d "$ROOT/.runtime/code-analyzer-keys" ]; then
  chmod 700 "$ROOT/.runtime/code-analyzer-keys" || true
  [ ! -f "$ROOT/.runtime/code-analyzer-keys/code-analyzer-private.pem" ] || chmod 600 "$ROOT/.runtime/code-analyzer-keys/code-analyzer-private.pem"
  [ ! -f "$ROOT/.runtime/code-analyzer-keys/code-analyzer-public.pem" ] || chmod 644 "$ROOT/.runtime/code-analyzer-keys/code-analyzer-public.pem"
fi

# Verify after installation so a partial package-manager failure cannot be
# mistaken for a configured node.
for cmd in "${required_commands[@]}" nc docker; do
  command -v "$cmd" >/dev/null 2>&1 || die "$cmd is still missing after automatic bootstrap"
done

log 'host dependencies ready'
