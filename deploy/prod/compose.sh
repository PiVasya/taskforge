#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
ROOT_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
cd "$ROOT_DIR"

TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR="${TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR:-$ROOT_DIR/.runtime/image-analyzer-model-cache}"
mkdir -p "$TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR" 2>/dev/null || true
chmod 0777 "$TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR" 2>/dev/null || true
export TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR
export TASKFORGE_ROOT="$ROOT_DIR"

ENV_FILE="${TASKFORGE_PROD_ENV_FILE:-deploy/prod/.env}"
if [ ! -f "$ENV_FILE" ]; then
  echo "Missing $ENV_FILE. Create it first:" >&2
  echo "  cp deploy/prod/.env.example deploy/prod/.env" >&2
  exit 2
fi

compose() {
  docker compose \
    --env-file "$ENV_FILE" \
    -f deploy/prod/compose/00-storage.yaml \
    -f deploy/prod/compose/10-apps-gateway.yaml \
    -f deploy/prod/compose/20-core-services.yaml \
    -f deploy/prod/compose/30-execution.yaml \
    -f deploy/prod/compose/40-ai-and-analyzers.yaml \
    -f deploy/prod/compose/50-integrations.yaml \
    -f deploy/prod/compose/80-watchtower.yaml \
    -f deploy/prod/compose/90-certbot.yaml \
    "$@"
}

new_log_dir() {
  base_dir="${TASKFORGE_PROD_LOG_DIR:-deploy/prod/logs}"
  run_id=$(date -u +"%Y%m%d-%H%M%S")
  log_dir="$base_dir/$run_id"
  mkdir -p "$log_dir"
  printf '%s\n' "$log_dir"
}

capture_window() {
  seconds="$1"
  log_dir="$2"
  start_ts="$3"
  log_file="$log_dir/startup-${seconds}s.log"
  ps_file="$log_dir/ps.txt"

  {
    echo "TaskForge prod startup log"
    echo "Started at UTC: $start_ts"
    echo "Capture seconds: $seconds"
    echo "Env file: $ENV_FILE"
    echo "Command: docker compose logs --since $start_ts --timestamps -f"
    echo "============================================================"
  } > "$log_file"

  echo "Capturing prod logs for ${seconds}s..."
  echo "Log file: $log_file"

  if command -v timeout >/dev/null 2>&1; then
    (timeout "${seconds}s" "$0" logs --since "$start_ts" --timestamps -f 2>&1 || true) | tee -a "$log_file"
  else
    (
      "$0" logs --since "$start_ts" --timestamps -f 2>&1 &
      child_pid=$!
      sleep "$seconds"
      kill "$child_pid" 2>/dev/null || true
      wait "$child_pid" 2>/dev/null || true
    ) | tee -a "$log_file"
  fi

  compose ps > "$ps_file" 2>&1 || true
  echo "Saved compose ps: $ps_file"
  echo "Saved logs: $log_file"
}

print_log_help() {
  cat <<'EOF_HELP'
TaskForge prod compose wrapper

Regular docker compose usage still works:
  ./deploy/prod/compose.sh pull
  ./deploy/prod/compose.sh up -d
  ./deploy/prod/compose.sh logs -f --tail=200

Extra logging commands:
  ./deploy/prod/compose.sh up-logs
      Start stack in detached mode and capture the first 30 seconds of logs
      from all services into deploy/prod/logs/<timestamp>/startup-30s.log.

  TASKFORGE_STARTUP_LOG_SECONDS=60 ./deploy/prod/compose.sh up-logs
      Same, but capture 60 seconds.

  ./deploy/prod/compose.sh logs-startup 30
      Do not start services. Capture the next 30 seconds of live logs.

  ./deploy/prod/compose.sh logs-dump 1000
      Save the last 1000 log lines from all services.

Environment variables:
  TASKFORGE_STARTUP_LOG_SECONDS=30
  TASKFORGE_PROD_LOG_DIR=deploy/prod/logs
  TASKFORGE_PROD_ENV_FILE=deploy/prod/.env
EOF_HELP
}

cmd="${1:-}"
case "$cmd" in
  up-logs|up-capture|up-watch)
    shift
    seconds="${TASKFORGE_STARTUP_LOG_SECONDS:-30}"
    start_ts=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    log_dir=$(new_log_dir)
    echo "Starting prod stack in detached mode..."
    compose up -d "$@"
    capture_window "$seconds" "$log_dir" "$start_ts"
    echo ""
    echo "Useful next commands:"
    echo "  ./deploy/prod/compose.sh ps"
    echo "  ./deploy/prod/compose.sh logs -f --tail=200"
    exit 0
    ;;
  logs-startup|logs-window)
    shift
    seconds="${1:-${TASKFORGE_STARTUP_LOG_SECONDS:-30}}"
    start_ts=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    log_dir=$(new_log_dir)
    capture_window "$seconds" "$log_dir" "$start_ts"
    exit 0
    ;;
  logs-dump)
    shift
    tail_lines="${1:-${TASKFORGE_LOG_TAIL:-1000}}"
    case "$tail_lines" in
      ''|*[!0-9]*)
        echo "logs-dump expects a numeric tail count as the first argument." >&2
        exit 2
        ;;
    esac
    shift || true
    log_dir=$(new_log_dir)
    log_file="$log_dir/logs-tail-${tail_lines}.log"
    ps_file="$log_dir/ps.txt"
    echo "Saving last ${tail_lines} prod log lines to $log_file"
    compose logs --timestamps --tail "$tail_lines" "$@" > "$log_file" 2>&1 || true
    compose ps > "$ps_file" 2>&1 || true
    echo "Saved compose ps: $ps_file"
    echo "Saved logs: $log_file"
    exit 0
    ;;
  help-logs|logs-help)
    print_log_help
    exit 0
    ;;
esac

exec docker compose \
  --env-file "$ENV_FILE" \
  -f deploy/prod/compose/00-storage.yaml \
  -f deploy/prod/compose/10-apps-gateway.yaml \
  -f deploy/prod/compose/20-core-services.yaml \
  -f deploy/prod/compose/30-execution.yaml \
  -f deploy/prod/compose/40-ai-and-analyzers.yaml \
  -f deploy/prod/compose/50-integrations.yaml \
  -f deploy/prod/compose/80-watchtower.yaml \
  -f deploy/prod/compose/90-certbot.yaml \
  "$@"
