#!/usr/bin/env bash
# Safe idempotent helpers for TaskForge replica-mode WireGuard TCP endpoints.
#
# Rules:
# - identify TaskForge-managed systemd units before looking at generic listeners;
# - never destroy an unknown/direct listener;
# - wait for systemd/socket transitions instead of sampling `ss` once;
# - never install a proxy unless its localhost backend is already listening;
# - write unit files atomically.

TASKFORGE_SYSTEMD_DIR="${TASKFORGE_SYSTEMD_DIR:-/etc/systemd/system}"
TASKFORGE_SYSTEMCTL="${TASKFORGE_SYSTEMCTL:-systemctl}"
TASKFORGE_SS="${TASKFORGE_SS:-ss}"
TASKFORGE_PROXY_READY_TIMEOUT="${TASKFORGE_PROXY_READY_TIMEOUT:-10}"

proxy_unit_name(){
  printf 'taskforge-%s-wg-proxy.service\n' "$1"
}

proxy_expected_exec(){
  local bind_ip="$1" cluster_port="$2" local_port="$3"
  printf '/usr/bin/socat TCP-LISTEN:%s,bind=%s,reuseaddr,fork TCP:127.0.0.1:%s\n' \
    "$cluster_port" "$bind_ip" "$local_port"
}

proxy_listener_present(){
  local bind_ip="$1" cluster_port="$2"
  "$TASKFORGE_SS" -H -ltn 2>/dev/null | awk '{print $4}' | grep -Fqx "$bind_ip:$cluster_port"
}

proxy_wait_listener(){
  local bind_ip="$1" cluster_port="$2" unit="${3:-}" timeout="${4:-$TASKFORGE_PROXY_READY_TIMEOUT}"
  local deadline=$((SECONDS + timeout))
  while [ "$SECONDS" -le "$deadline" ]; do
    if proxy_listener_present "$bind_ip" "$cluster_port"; then
      return 0
    fi
    # A unit can briefly be "activating". Only fail early when systemd reports
    # an explicitly failed/inactive state after it has had a chance to start.
    if [ -n "$unit" ]; then
      case "$("$TASKFORGE_SYSTEMCTL" is-active "$unit" 2>/dev/null || true)" in
        failed|inactive) return 1 ;;
      esac
    fi
    sleep 0.2
  done
  return 1
}

proxy_wait_listener_absent(){
  local bind_ip="$1" cluster_port="$2" timeout="${3:-$TASKFORGE_PROXY_READY_TIMEOUT}"
  local deadline=$((SECONDS + timeout))
  while [ "$SECONDS" -le "$deadline" ]; do
    proxy_listener_present "$bind_ip" "$cluster_port" || return 0
    sleep 0.2
  done
  return 1
}

proxy_wait_unit_inactive(){
  local unit="$1" timeout="${2:-$TASKFORGE_PROXY_READY_TIMEOUT}" deadline
  deadline=$((SECONDS + timeout))
  while [ "$SECONDS" -le "$deadline" ]; do
    if ! "$TASKFORGE_SYSTEMCTL" is-active --quiet "$unit" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.2
  done
  return 1
}

proxy_unit_file(){
  printf '%s/%s\n' "$TASKFORGE_SYSTEMD_DIR" "$1"
}

proxy_unit_matches(){
  local unit="$1" expected_exec="$2" file
  file="$(proxy_unit_file "$unit")"
  [ -f "$file" ] && grep -Fqx "ExecStart=$expected_exec" "$file"
}

proxy_unit_active(){
  "$TASKFORGE_SYSTEMCTL" is-active --quiet "$1" >/dev/null 2>&1
}

proxy_unit_known(){
  local unit="$1" file
  file="$(proxy_unit_file "$unit")"
  [ -e "$file" ] || [ -L "$file" ] || proxy_unit_active "$unit"
}

proxy_remove_managed_unit(){
  local unit="$1" bind_ip="${2:-}" cluster_port="${3:-}" file
  file="$(proxy_unit_file "$unit")"
  "$TASKFORGE_SYSTEMCTL" disable --now "$unit" >/dev/null 2>&1 || true
  if ! proxy_wait_unit_inactive "$unit"; then
    printf 'refusing to remove %s: unit did not stop within %ss\n' "$unit" "$TASKFORGE_PROXY_READY_TIMEOUT" >&2
    return 1
  fi
  if [ -n "$bind_ip" ] && [ -n "$cluster_port" ] && ! proxy_wait_listener_absent "$bind_ip" "$cluster_port"; then
    printf 'refusing to remove %s: managed listener %s:%s did not disappear within %ss\n' \
      "$unit" "$bind_ip" "$cluster_port" "$TASKFORGE_PROXY_READY_TIMEOUT" >&2
    return 1
  fi
  rm -f "$file"
  "$TASKFORGE_SYSTEMCTL" daemon-reload >/dev/null 2>&1 || true
}

proxy_write_unit(){
  local name="$1" bind_ip="$2" cluster_port="$3" local_port="$4" unit="$5"
  local file expected_exec tmp
  file="$(proxy_unit_file "$unit")"
  expected_exec="$(proxy_expected_exec "$bind_ip" "$cluster_port" "$local_port")"
  mkdir -p "$TASKFORGE_SYSTEMD_DIR"
  tmp="$(mktemp "$TASKFORGE_SYSTEMD_DIR/.${unit}.XXXXXX")"
  chmod 0644 "$tmp"
  cat > "$tmp" <<EOF_UNIT
[Unit]
Description=TaskForge $name WireGuard TCP proxy
After=network-online.target wg-quick@wg-taskforge.service docker.service
Wants=network-online.target wg-quick@wg-taskforge.service

[Service]
ExecStart=$expected_exec
Restart=always
RestartSec=2
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true

[Install]
WantedBy=multi-user.target
EOF_UNIT
  mv -f "$tmp" "$file"
  "$TASKFORGE_SYSTEMCTL" daemon-reload >/dev/null
  "$TASKFORGE_SYSTEMCTL" enable --now "$unit" >/dev/null
}

# Reconcile one endpoint and print one of:
#   managed-existing - expected TaskForge proxy already healthy
#   managed-created  - proxy created/repaired and verified
#   direct           - a non-managed listener owns the WG endpoint; preserved
# Return non-zero if the endpoint cannot be reconciled safely.
proxy_reconcile(){
  local name="$1" bind_ip="$2" cluster_port="$3" local_port="$4"
  local unit expected_exec
  unit="$(proxy_unit_name "$name")"
  expected_exec="$(proxy_expected_exec "$bind_ip" "$cluster_port" "$local_port")"

  # Identify our unit before generic listeners. This prevents the v35 bug where
  # TaskForge saw its own socat and deleted it as if it were a direct listener.
  if proxy_unit_matches "$unit" "$expected_exec" && proxy_unit_active "$unit"; then
    if ! proxy_listener_present "$bind_ip" "$cluster_port"; then
      "$TASKFORGE_SYSTEMCTL" restart "$unit" >/dev/null 2>&1 || {
        printf 'failed to restart %s\n' "$unit" >&2
        return 1
      }
    fi
    if proxy_wait_listener "$bind_ip" "$cluster_port" "$unit"; then
      printf 'managed-existing\n'
      return 0
    fi
    printf '%s is active but %s:%s did not become ready within %ss\n' \
      "$unit" "$bind_ip" "$cluster_port" "$TASKFORGE_PROXY_READY_TIMEOUT" >&2
    return 1
  fi

  # Remove only a TaskForge-owned stale/mismatched unit. Never stop an unknown
  # listener. Wait until the old listener is actually gone before proceeding.
  if proxy_unit_known "$unit"; then
    proxy_remove_managed_unit "$unit" "$bind_ip" "$cluster_port" || return 1
  fi

  if proxy_listener_present "$bind_ip" "$cluster_port"; then
    printf 'direct\n'
    return 0
  fi

  # Managed socat forwards to localhost. Do not create a healthy-looking socket
  # in front of a missing Docker/backend listener.
  if ! proxy_wait_listener 127.0.0.1 "$local_port" "" "$TASKFORGE_PROXY_READY_TIMEOUT"; then
    printf 'cannot create %s proxy: localhost backend 127.0.0.1:%s is not listening\n' "$name" "$local_port" >&2
    return 1
  fi

  if ! proxy_write_unit "$name" "$bind_ip" "$cluster_port" "$local_port" "$unit"; then
    printf 'failed to install/start %s\n' "$unit" >&2
    return 1
  fi
  if ! proxy_wait_listener "$bind_ip" "$cluster_port" "$unit"; then
    printf '%s started but %s:%s did not become ready within %ss\n' \
      "$unit" "$bind_ip" "$cluster_port" "$TASKFORGE_PROXY_READY_TIMEOUT" >&2
    return 1
  fi
  printf 'managed-created\n'
}
