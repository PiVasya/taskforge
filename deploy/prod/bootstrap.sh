#!/usr/bin/env bash
set -Eeuo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "$SCRIPT_DIR/compose.sh" ] && [ -f "$SCRIPT_DIR/cluster/easy.sh" ]; then
  ROOT="$SCRIPT_DIR"
  CHECK="$ROOT/check.sh"
  EASY="$ROOT/cluster/easy.sh"
  ENSURE_HOST="$ROOT/cluster/ops/host/ensure-host.sh"
  ENV_FILE="$ROOT/.env"
else
  ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
  CHECK="$ROOT/scripts/prod/check-prod-config.sh"
  EASY="$ROOT/deploy/cluster/easy.sh"
  ENSURE_HOST="$ROOT/deploy/cluster/ops/host/ensure-host.sh"
  ENV_FILE="$ROOT/deploy/prod/.env"
fi
[ "$(id -u)" -eq 0 ] || { echo "error: run sudo ./bootstrap.sh" >&2; exit 2; }
OWNER="${SUDO_USER:-$(stat -c '%U' "$ROOT")}"; [ "$OWNER" != root ] || OWNER="$(stat -c '%U' "$ROOT")"

mode=full
quiet=''
while [ $# -gt 0 ]; do
  case "$1" in
    --ensure|--if-needed) mode=ensure;;
    --quiet) quiet=--quiet;;
    *) echo "error: unknown bootstrap option: $1" >&2; exit 2;;
  esac
  shift
done

if [ "$mode" = full ]; then echo '[1/3] Host dependencies'; fi
[ -x "$ENSURE_HOST" ] || { echo "error: missing $ENSURE_HOST" >&2; exit 2; }
"$ENSURE_HOST" ${quiet:+$quiet}

# Lightweight automatic mode is used by adopt/apply/join/repair. It only
# guarantees host prerequisites and permissions; it does not run validation
# that may legitimately fail before the node has been adopted.
if [ "$mode" = ensure ]; then
  exit 0
fi

echo '[2/3] CPU compatibility'
"$EASY" cpu-profile --write

echo '[3/3] Configuration'
if [ -s "$ENV_FILE" ]; then
  sudo -u "$OWNER" "$CHECK"
else
  echo 'Skipped: .env is not present yet.'
fi

echo
echo 'TaskForge bootstrap complete.'
docker --version || true
docker compose version || true
echo 'Docker group membership is persistent. If this was the first Docker install, reconnect SSH/VS Code once before using docker as a non-root user.'
