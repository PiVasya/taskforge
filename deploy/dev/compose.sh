#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
ROOT_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
cd "$ROOT_DIR"

ENV_FILE="${TASKFORGE_DEV_ENV_FILE:-deploy/dev/.env}"
if [ ! -f "$ENV_FILE" ]; then
  echo "Missing $ENV_FILE. Create it first:" >&2
  echo "  cp deploy/dev/.env.example deploy/dev/.env" >&2
  exit 2
fi

compose() {
  docker compose \
    --env-file "$ENV_FILE" \
    -f deploy/dev/compose/00-storage.yaml \
    -f deploy/dev/compose/10-apps-gateway.yaml \
    -f deploy/dev/compose/20-core-services.yaml \
    -f deploy/dev/compose/30-execution.yaml \
    -f deploy/dev/compose/40-ai-and-analyzers.yaml \
    -f deploy/dev/compose/50-integrations.yaml \
    "$@"
}

new_log_dir() {
  base_dir="${TASKFORGE_DEV_LOG_DIR:-deploy/dev/logs}"
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
    echo "TaskForge dev startup log"
    echo "Started at UTC: $start_ts"
    echo "Capture seconds: $seconds"
    echo "Env file: $ENV_FILE"
    echo "Command: docker compose logs --since $start_ts --timestamps -f"
    echo "============================================================"
  } > "$log_file"

  echo "Capturing dev logs for ${seconds}s..."
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
TaskForge dev compose wrapper

Regular docker compose usage still works:
  ./deploy/dev/compose.sh up --build
  ./deploy/dev/compose.sh logs -f --tail=200
  ./deploy/dev/compose.sh ps

Extra logging commands:
  ./deploy/dev/compose.sh up-logs --build
      Start stack in detached mode and capture the first 30 seconds of logs
      from all services into deploy/dev/logs/<timestamp>/startup-30s.log.

  TASKFORGE_STARTUP_LOG_SECONDS=60 ./deploy/dev/compose.sh up-logs --build
      Same, but capture 60 seconds.

  ./deploy/dev/compose.sh logs-startup 30
      Do not start services. Capture the next 30 seconds of live logs.

  ./deploy/dev/compose.sh logs-dump 1000
      Save the last 1000 log lines from all services.

  ./deploy/dev/compose.sh logs-dump 300 ai-worker
      Save the last 300 log lines only for ai-worker.

Environment variables:
  TASKFORGE_STARTUP_LOG_SECONDS=30
  TASKFORGE_DEV_LOG_DIR=deploy/dev/logs
  TASKFORGE_DEV_ENV_FILE=deploy/dev/.env
EOF_HELP
}

cmd="${1:-}"
case "$cmd" in
  up-logs|up-capture|up-watch)
    shift
    seconds="${TASKFORGE_STARTUP_LOG_SECONDS:-30}"
    start_ts=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    log_dir=$(new_log_dir)
    echo "Starting dev stack in detached mode..."
    compose up -d "$@"
    capture_window "$seconds" "$log_dir" "$start_ts"
    echo ""
    echo "Useful next commands:"
    echo "  ./deploy/dev/compose.sh ps"
    echo "  ./deploy/dev/compose.sh logs -f --tail=200"
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
    echo "Saving last ${tail_lines} dev log lines to $log_file"
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
  -f deploy/dev/compose/00-storage.yaml \
  -f deploy/dev/compose/10-apps-gateway.yaml \
  -f deploy/dev/compose/20-core-services.yaml \
  -f deploy/dev/compose/30-execution.yaml \
  -f deploy/dev/compose/40-ai-and-analyzers.yaml \
  -f deploy/dev/compose/50-integrations.yaml \
  "$@"
