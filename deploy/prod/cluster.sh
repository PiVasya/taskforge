#!/usr/bin/env bash
set -Eeuo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Some GUI extractors/filesystems can drop executable bits from tar contents.
# If cluster.sh is started as `bash cluster.sh ...`, repair script modes once so
# subsequent invocations can use the normal `bash ./cluster.sh ...` form.
find "$SCRIPT_DIR" -type f -name '*.sh' -exec chmod u+x {} + 2>/dev/null || true
if [ -x "$SCRIPT_DIR/cluster/easy.sh" ]; then
  ROOT="$SCRIPT_DIR"; CLUSTER="$ROOT/cluster"; BOOTSTRAP="$ROOT/bootstrap.sh"
else
  ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"; CLUSTER="$ROOT/deploy/cluster"; BOOTSTRAP="$ROOT/deploy/prod/bootstrap.sh"
fi

run_root(){
  if [ "$(id -u)" -eq 0 ]; then "$@"; else sudo "$@"; fi
}
exec_root(){
  if [ "$(id -u)" -eq 0 ]; then exec "$@"; else exec sudo "$@"; fi
}
ensure_host(){ run_root "$BOOTSTRAP" --ensure --quiet; }

command="${1:-help}"; [ $# -eq 0 ] || shift
case "$command" in
  bootstrap) exec_root "$BOOTSTRAP" "$@";;
  prepare-node|import-topology|apply|adopt|init-primary|join|migrate-local|promote|repair)
    exec_root "$CLUSTER/easy.sh" "$command" "$@";;
  add-node|sync-minio) exec "$CLUSTER/easy.sh" "$command" "$@";;
  # WireGuard runtime peer/handshake metadata requires CAP_NET_ADMIN on Linux.
  # Elevate diagnostics automatically so a normal `bash ./cluster.sh doctor/status`
  # never reports a false peers=0 just because `wg show` was unprivileged.
  status|doctor) exec_root "$CLUSTER/easy.sh" "$command" "$@";;
  export-secrets) ensure_host; exec_root "$CLUSTER/export-secrets.sh" "$@";;
  import-secrets) ensure_host; exec_root "$CLUSTER/import-secrets.sh" "$@";;
  quorum-prepare) exec_root "$CLUSTER/easy.sh" quorum prepare "$@";;
  quorum-enable-primary) exec_root "$CLUSTER/easy.sh" quorum enable-primary "$@";;
  quorum-join) exec_root "$CLUSTER/easy.sh" quorum join "$@";;
  help|-h|--help)
    cat <<'TXT'
TaskForge cluster manager

Existing A/B migration (recommended):
  # dependencies, permissions, .env/key migration and adoption are automatic
  bash ./cluster.sh migrate-local A --from /path/to/old-server-folder
  bash ./cluster.sh migrate-local B --from /path/to/old-server-folder

Already copied .env/keys into this folder:
  bash ./cluster.sh adopt A
  bash ./cluster.sh adopt B
  bash ./cluster.sh status
  bash ./cluster.sh doctor

Fresh A/B:
  bash ./cluster.sh bootstrap
  bash ./cluster.sh init-primary A
  bash ./cluster.sh join B --yes

Add C or D:
  # on the new server; host dependencies are installed automatically by the command
  bash ./cluster.sh prepare-node --node C --public-ip IP --wg-ip 10.80.0.3 [ports]

  # copy descriptor to A
  bash ./cluster.sh add-node taskforge-node-C.json

  # copy taskforge-cluster-topology.json to all nodes
  bash ./cluster.sh import-topology taskforge-cluster-topology.json
  bash ./cluster.sh apply NODE_ID

  # on C
  bash ./cluster.sh join C --yes

Maintenance:
  bash ./cluster.sh sync-minio --yes
  bash ./cluster.sh promote --old-primary A --confirm-old-primary-off
  bash ./cluster.sh repair
  bash ./cluster.sh export-secrets FILE
  bash ./cluster.sh import-secrets FILE

Automatic failover after three independent voters exist:
  bash ./cluster.sh quorum-prepare          # every voter
  bash ./cluster.sh quorum-enable-primary   # A after every voter is ready
  bash ./cluster.sh quorum-join             # B/C/D

Notes:
  - socat, WireGuard tools, jq, curl and other host packages are auto-installed.
  - Docker is auto-installed when a mutating cluster command needs it.
  - after the first Docker group addition, reconnect SSH/VS Code once before using docker as a non-root user.
TXT
    ;;
  *) echo "error: unknown command $command; run bash ./cluster.sh help" >&2; exit 2;;
esac
