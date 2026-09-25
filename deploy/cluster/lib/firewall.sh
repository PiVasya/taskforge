#!/usr/bin/env bash
# TaskForge UFW policy helpers.
#
# Deployment invariant:
# - UFW is installed and enabled automatically by mutating cluster commands;
# - the current SSH service is allowed BEFORE UFW is enabled;
# - public web ports are limited to Cloudflare by default;
# - PostgreSQL/MinIO/cluster health stay on WireGuard only;
# - existing UFW installations are not reset.

TASKFORGE_UFW="${TASKFORGE_UFW:-ufw}"
TASKFORGE_FIREWALL_WEB_SOURCE="${TASKFORGE_FIREWALL_WEB_SOURCE:-cloudflare}"
TASKFORGE_FIREWALL_AUTO_ENABLE="${TASKFORGE_FIREWALL_AUTO_ENABLE:-1}"
TASKFORGE_FIREWALL_DATA_DIR="${TASKFORGE_FIREWALL_DATA_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../data" && pwd)}"
TASKFORGE_CF_V4_FILE="${TASKFORGE_CF_V4_FILE:-$TASKFORGE_FIREWALL_DATA_DIR/cloudflare-ips-v4.txt}"
TASKFORGE_CF_V6_FILE="${TASKFORGE_CF_V6_FILE:-$TASKFORGE_FIREWALL_DATA_DIR/cloudflare-ips-v6.txt}"

firewall_is_active(){
  "$TASKFORGE_UFW" status 2>/dev/null | grep -Eq '^Status:[[:space:]]+active$'
}

firewall_ipv6_enabled(){
  [ -r /etc/default/ufw ] && grep -Eqi '^IPV6[[:space:]]*=[[:space:]]*yes([[:space:]]|$)' /etc/default/ufw
}

firewall_validate_port(){
  case "$1" in
    ''|*[!0-9]*) return 1;;
  esac
  [ "$1" -ge 1 ] && [ "$1" -le 65535 ]
}

firewall_detect_ssh_ports(){
  local candidate detected
  detected="$({
    # Best signal when invoked from the SSH session that could otherwise be cut.
    if [ -n "${SSH_CONNECTION:-}" ]; then
      candidate="${SSH_CONNECTION##* }"
      firewall_validate_port "$candidate" && printf '%s\n' "$candidate"
    fi

    # Effective sshd configuration covers custom Port directives/includes.
    if command -v sshd >/dev/null 2>&1; then
      sshd -T 2>/dev/null | awk '$1=="port" {print $2}' || true
    elif [ -x /usr/sbin/sshd ]; then
      /usr/sbin/sshd -T 2>/dev/null | awk '$1=="port" {print $2}' || true
    fi

    # Config fallback for systems where `sshd -T` cannot run before first start.
    grep -hE '^[[:space:]]*Port[[:space:]]+[0-9]+' \
      /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*.conf 2>/dev/null \
      | awk '{print $2}' || true

    # Socket/process fallback. Root callers get process names from ss -p.
    if command -v ss >/dev/null 2>&1; then
      ss -H -lntp 2>/dev/null \
        | awk '$0 ~ /(sshd|ssh\.socket)/ {a=$4; sub(/^.*:/,"",a); gsub(/\]/,"",a); print a}' || true
    fi
  } | awk '/^[0-9]+$/ && $1>=1 && $1<=65535 {seen[$1]=1} END {for (p in seen) print p}' | sort -n)"

  # Default only when no effective/current SSH port could be discovered.
  if [ -n "$detected" ]; then
    printf '%s\n' "$detected"
  else
    printf '22\n'
  fi
}

firewall_allow_ssh(){
  local port count=0
  while IFS= read -r port; do
    [ -n "$port" ] || continue
    "$TASKFORGE_UFW" allow "$port/tcp" comment 'TaskForge SSH' >/dev/null
    count=$((count+1))
  done < <(firewall_detect_ssh_ports)
  [ "$count" -gt 0 ] || {
    printf 'error: could not determine any SSH port; refusing to enable UFW\n' >&2
    return 1
  }
}

firewall_allow_cloudflare_port(){
  local port="$1" cidr
  [ -s "$TASKFORGE_CF_V4_FILE" ] || {
    printf 'error: missing Cloudflare IPv4 list: %s\n' "$TASKFORGE_CF_V4_FILE" >&2
    return 1
  }
  while IFS= read -r cidr; do
    [ -n "$cidr" ] || continue
    "$TASKFORGE_UFW" allow proto tcp from "$cidr" to any port "$port" comment 'TaskForge Cloudflare web' >/dev/null
  done < "$TASKFORGE_CF_V4_FILE"

  if firewall_ipv6_enabled && [ -s "$TASKFORGE_CF_V6_FILE" ]; then
    while IFS= read -r cidr; do
      [ -n "$cidr" ] || continue
      "$TASKFORGE_UFW" allow proto tcp from "$cidr" to any port "$port" comment 'TaskForge Cloudflare web v6' >/dev/null
    done < "$TASKFORGE_CF_V6_FILE"
  fi
}

firewall_allow_public_web(){
  local http_port="$1" https_port="$2" port
  case "$TASKFORGE_FIREWALL_WEB_SOURCE" in
    cloudflare)
      for port in "$http_port" "$https_port"; do
        firewall_validate_port "$port" || {
          printf 'error: invalid web port: %s\n' "$port" >&2
          return 1
        }
        firewall_allow_cloudflare_port "$port" || return 1
      done
      ;;
    any)
      for port in "$http_port" "$https_port"; do
        firewall_validate_port "$port" || return 1
        "$TASKFORGE_UFW" allow "$port/tcp" comment 'TaskForge public web' >/dev/null
      done
      ;;
    none)
      ;;
    *)
      printf 'error: TASKFORGE_FIREWALL_WEB_SOURCE must be cloudflare, any, or none (got %s)\n' \
        "$TASKFORGE_FIREWALL_WEB_SOURCE" >&2
      return 1
      ;;
  esac
}

firewall_enable_if_needed(){
  firewall_is_active && return 0
  [ "$TASKFORGE_FIREWALL_AUTO_ENABLE" = 1 ] || {
    printf 'warning: UFW is inactive and TASKFORGE_FIREWALL_AUTO_ENABLE=%s; leaving it disabled\n' \
      "$TASKFORGE_FIREWALL_AUTO_ENABLE" >&2
    return 0
  }

  # Rules must already be staged by the caller. Do not `ufw reset`: operators
  # may have additional valid rules that must survive TaskForge upgrades.
  "$TASKFORGE_UFW" default deny incoming >/dev/null
  "$TASKFORGE_UFW" default allow outgoing >/dev/null
  "$TASKFORGE_UFW" --force enable >/dev/null
  firewall_is_active || {
    printf 'error: UFW did not become active after enable\n' >&2
    return 1
  }
}
