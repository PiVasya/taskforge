#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
ha_need_root
ha_need_env
force=0
[ "${1:-}" = "--force" ] && force=1
if [ "$force" -ne 1 ] && [ ! -f "$TASKFORGE_ROOT/.runtime/ha/traffic-ready" ]; then
  echo "Local node is not active; shared-volume sync skipped."
  exit 0
fi
peer="$(ha_read_env HA_PEER_WG_IP)"
ssh_user="$(ha_read_env HA_PEER_SSH_USER)"; ssh_user="${ssh_user:-root}"
ssh_port="$(ha_read_env HA_PEER_SSH_PORT)"; ssh_port="${ssh_port:-22}"
key="$(ha_read_env HA_PEER_SSH_KEY)"; key="${key:-$TASKFORGE_ROOT/.runtime/ha/ssh/id_ed25519}"
helper="$(ha_read_env HA_SYNC_HELPER_IMAGE)"; helper="${helper:-alpine:3.22}"
project="$(ha_read_env COMPOSE_PROJECT_NAME)"; project="${project:-taskforge-prod}"
[[ "$project" =~ ^[A-Za-z0-9_.-]+$ ]] || ha_die "invalid COMPOSE_PROJECT_NAME"
[ -s "$key" ] || { echo "HA SSH key not configured; shared-volume sync skipped."; exit 0; }
ssh_opts=(-i "$key" -p "$ssh_port" -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new)

# These volumes contain TLS and ASP.NET key material that must follow the active node.
volumes=("${project}_letsencrypt" "${project}_content-data-protection" "${project}_quiz-data-protection")
for volume in "${volumes[@]}"; do
  if ! docker volume inspect "$volume" >/dev/null 2>&1; then
    echo "skip missing local volume: $volume"
    continue
  fi
  echo "sync $volume -> $ssh_user@$peer"
  ssh "${ssh_opts[@]}" "$ssh_user@$peer" "docker volume create '$volume' >/dev/null && docker pull '$helper' >/dev/null 2>&1 || true"
  docker run --rm -v "$volume:/data:ro" "$helper" sh -ec 'cd /data && tar czf - .' |
    ssh "${ssh_opts[@]}" "$ssh_user@$peer" \
      "docker run --rm -i -v '$volume:/data' '$helper' sh -ec 'find /data -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +; cd /data; tar xzf -'"
done
echo "Shared-volume sync complete."
