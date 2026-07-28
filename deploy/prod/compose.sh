#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$ROOT_DIR"

TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR="${TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR:-$ROOT_DIR/.runtime/image-analyzer-model-cache}"
mkdir -p "$TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR" "$ROOT_DIR/.runtime"
chmod 0777 "$TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR" 2>/dev/null || true
export TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR
export TASKFORGE_ROOT="$ROOT_DIR"

ENV_FILE="${TASKFORGE_PROD_ENV_FILE:-deploy/prod/.env}"
[ -f "$ENV_FILE" ] || { echo "Missing $ENV_FILE. Run scripts/prod/prepare-env.sh first." >&2; exit 2; }

COMPOSE_FILES=(
  -f deploy/prod/compose/00-storage.yaml
  -f deploy/prod/compose/10-apps-gateway.yaml
  -f deploy/prod/compose/20-core-services.yaml
  -f deploy/prod/compose/30-execution.yaml
  -f deploy/prod/compose/40-ai-and-analyzers.yaml
  -f deploy/prod/compose/50-integrations.yaml
  -f deploy/prod/compose/80-watchtower.yaml
  -f deploy/prod/compose/90-certbot.yaml
)

AUTH_CONFIG_PATH=""
if [ -f "$ROOT_DIR/config.json" ]; then
  mkdir -p "$ROOT_DIR/.docker"
  cp "$ROOT_DIR/config.json" "$ROOT_DIR/.docker/config.json"
  chmod 600 "$ROOT_DIR/.docker/config.json" 2>/dev/null || true
  export DOCKER_CONFIG="$ROOT_DIR/.docker"
  AUTH_CONFIG_PATH="$ROOT_DIR/.docker/config.json"
elif [ -n "${DOCKER_CONFIG:-}" ] && [ -f "${DOCKER_CONFIG}/config.json" ]; then
  AUTH_CONFIG_PATH="$(cd "$(dirname "${DOCKER_CONFIG}/config.json")" && pwd)/config.json"
elif [ -f "${HOME:-}/.docker/config.json" ]; then
  AUTH_CONFIG_PATH="$(cd "$(dirname "${HOME}/.docker/config.json")" && pwd)/config.json"
fi
if [ -n "$AUTH_CONFIG_PATH" ]; then
  python3 - "$ROOT_DIR/.runtime/watchtower-auth.yaml" "$AUTH_CONFIG_PATH" <<'PY'
from pathlib import Path
import json,sys
Path(sys.argv[1]).write_text(
    "services:\n  watchtower:\n    volumes:\n      - " + json.dumps(sys.argv[2]+":/config.json:ro") + "\n",
    encoding="utf-8",
)
PY
  COMPOSE_FILES+=( -f .runtime/watchtower-auth.yaml )
fi

compose(){ docker compose --env-file "$ENV_FILE" "${COMPOSE_FILES[@]}" "$@"; }
new_log_dir(){
  local base="${TASKFORGE_PROD_LOG_DIR:-deploy/prod/logs}"
  local dir="$base/$(date -u +%Y%m%d-%H%M%S)"
  mkdir -p "$dir"; printf '%s\n' "$dir"
}
capture_window(){
  local seconds="$1" dir="$2" start="$3" log="$2/startup-${1}s.log"
  {
    echo "TaskForge production startup log"
    echo "Started at UTC: $start"
    echo "Capture seconds: $seconds"
    echo "Environment file: $ENV_FILE"
    echo "============================================================"
  } > "$log"
  echo "Capturing ${seconds}s of logs into $log"
  if command -v timeout >/dev/null 2>&1; then
    (timeout "${seconds}s" "$0" logs --since "$start" --timestamps -f 2>&1 || true) | tee -a "$log"
  else
    ( "$0" logs --since "$start" --timestamps -f 2>&1 & pid=$!; sleep "$seconds"; kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true ) | tee -a "$log"
  fi
  compose ps > "$dir/ps.txt" 2>&1 || true
}

case "${1:-}" in
  up-logs|up-capture|up-watch)
    shift
    seconds="${TASKFORGE_STARTUP_LOG_SECONDS:-30}"
    start="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    dir="$(new_log_dir)"
    compose up -d "$@"
    capture_window "$seconds" "$dir" "$start"
    ;;
  logs-startup|logs-window)
    shift
    seconds="${1:-${TASKFORGE_STARTUP_LOG_SECONDS:-30}}"
    case "$seconds" in ''|*[!0-9]*) echo "error: duration must be numeric" >&2; exit 2;; esac
    capture_window "$seconds" "$(new_log_dir)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    ;;
  logs-dump)
    shift
    lines="${1:-${TASKFORGE_LOG_TAIL:-1000}}"
    case "$lines" in ''|*[!0-9]*) echo "error: line count must be numeric" >&2; exit 2;; esac
    shift || true
    dir="$(new_log_dir)"
    compose logs --timestamps --tail "$lines" "$@" > "$dir/logs-tail-${lines}.log" 2>&1 || true
    compose ps > "$dir/ps.txt" 2>&1 || true
    echo "Saved logs: $dir"
    ;;
  *) compose "$@" ;;
esac
