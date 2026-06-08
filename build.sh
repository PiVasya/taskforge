#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${TASKFORGE_DEV_ENV_FILE:-deploy/dev/.env}"
LOG_ROOT="${TASKFORGE_BUILD_LOG_DIR:-deploy/dev/build-logs}"
RUN_ID="${TASKFORGE_BUILD_RUN_ID:-$(date -u +%Y%m%d-%H%M%S)}"
LOG_DIR="$LOG_ROOT/$RUN_ID"
BUILD_TAIL_LINES="${TASKFORGE_BUILD_TAIL_LINES:-140}"
STARTUP_TAIL_LINES="${TASKFORGE_STARTUP_TAIL_LINES:-120}"
STARTUP_WAIT_SECONDS="${TASKFORGE_STARTUP_WAIT_SECONDS:-8}"
STATE_DIR="deploy/dev/.build-state"
CURRENT_TARGETS=()
CURRENT_BUILD_MODE="selected"

COMPOSE_FILES=(
  -f deploy/dev/compose/00-storage.yaml
  -f deploy/dev/compose/10-apps-gateway.yaml
  -f deploy/dev/compose/20-core-services.yaml
  -f deploy/dev/compose/30-execution.yaml
  -f deploy/dev/compose/40-ai-and-analyzers.yaml
  -f deploy/dev/compose/50-integrations.yaml
)

ALL_SERVICES=(
  gateway front front-ct
  identity-api education-api content-api quiz-api tasks-api solutions-api rating-worker
  execution-api execution-worker csharp-runner cpp-runner java-runner javascript-runner pascal-runner python-runner image-cpp-runner image-pascal-runner
  ai-api ai-worker code-analyzer image-analyzer
  support-api support-bot minecraft-api files-api notifications-api observability-api telegram-quiz-bot
)

BACKEND_SERVICES=(
  identity-api education-api content-api quiz-api tasks-api solutions-api rating-worker
  execution-api execution-worker
  ai-api ai-worker
  support-api support-bot minecraft-api files-api notifications-api observability-api telegram-quiz-bot
)

FRONTEND_SERVICES=(gateway front front-ct)

JUDGE_SERVICES=(
  tasks-api solutions-api execution-api execution-worker code-analyzer
  csharp-runner cpp-runner java-runner javascript-runner pascal-runner python-runner
)

RUNNER_SERVICES=(csharp-runner cpp-runner java-runner javascript-runner pascal-runner python-runner image-cpp-runner image-pascal-runner)
AI_SERVICES=(ai-api ai-worker code-analyzer image-analyzer)
MAIN_LOG_SERVICES=(gateway front front-ct identity-api tasks-api solutions-api execution-api execution-worker code-analyzer csharp-runner cpp-runner java-runner javascript-runner pascal-runner python-runner)

compose() {
  docker compose --env-file "$ENV_FILE" "${COMPOSE_FILES[@]}" "$@"
}

say() { printf '\n==> %s\n' "$*"; }
warn() { printf '\n!! %s\n' "$*" >&2; }

ensure_env() {
  if [ ! -f "$ENV_FILE" ]; then
    mkdir -p "$(dirname "$ENV_FILE")"
    cp deploy/dev/.env.example "$ENV_FILE"
    say "Created $ENV_FILE from deploy/dev/.env.example"
  fi
}

usage() {
  cat <<'EOF_HELP'
TaskForge build helper

Main commands:
  ./build.sh
      Build all app images one by one, start the stack, then show useful logs.

  ./build.sh tasks-api
  ./build.sh tasks-api execution-worker
      Build only the listed service(s), restart them, then show their logs.

  ./build.sh resume
      Continue from the service that failed in the previous full/group build.

Groups:
  ./build.sh backend
  ./build.sh frontend
  ./build.sh judge
  ./build.sh runners
  ./build.sh ai

Logs:
  ./build.sh logs
  ./build.sh logs judge
  ./build.sh logs tasks-api execution-worker

Other:
  ./build.sh up              Start already built stack, no build.
  ./build.sh down            Stop stack.
  ./build.sh ps              Show containers.
  ./build.sh clean           Remove local bin/obj/build logs, not Docker volumes.
  ./build.sh e2e             Run scripts/dev-judge-e2e.sh.

Useful env:
  TASKFORGE_BUILD_TAIL_LINES=220 ./build.sh tasks-api
  TASKFORGE_STARTUP_TAIL_LINES=200 ./build.sh
EOF_HELP
}

expand_targets() {
  local out=()
  if [ "$#" -eq 0 ]; then
    printf '%s\n' "${ALL_SERVICES[@]}"
    return 0
  fi

  local arg
  for arg in "$@"; do
    case "$arg" in
      all) out+=("${ALL_SERVICES[@]}") ;;
      backend) out+=("${BACKEND_SERVICES[@]}") ;;
      frontend) out+=("${FRONTEND_SERVICES[@]}") ;;
      judge) out+=("${JUDGE_SERVICES[@]}") ;;
      runners) out+=("${RUNNER_SERVICES[@]}") ;;
      ai) out+=("${AI_SERVICES[@]}") ;;
      *) out+=("$arg") ;;
    esac
  done

  printf '%s\n' "${out[@]}" | awk '!seen[$0]++'
}

save_resume_state() {
  local failed_service="$1"
  mkdir -p "$STATE_DIR"
  printf '%s
' "$failed_service" > "$STATE_DIR/failed-service"
  printf '%s
' "$CURRENT_BUILD_MODE" > "$STATE_DIR/mode"
  printf '%s
' "${CURRENT_TARGETS[@]}" > "$STATE_DIR/targets"
}

clear_resume_state() {
  rm -rf "$STATE_DIR"
}

print_failure() {
  local service="$1"
  local log_file="$2"
  warn "BUILD FAILED: $service"
  echo ""
  echo "Last $BUILD_TAIL_LINES lines from: $log_file"
  echo "------------------------------------------------------------"
  tail -n "$BUILD_TAIL_LINES" "$log_file" || true
  echo "------------------------------------------------------------"
  echo "Full log: $log_file"
  echo "Repeat only this service: ./build.sh $service"
  echo "Continue from failed service: ./build.sh resume"
  echo "More lines: TASKFORGE_BUILD_TAIL_LINES=240 ./build.sh resume"
}

build_one() {
  local service="$1"
  local log_file="$LOG_DIR/build-$service.log"
  mkdir -p "$LOG_DIR"

  printf '\n[%s] build %s\n' "$(date +%H:%M:%S)" "$service"
  echo "log: $log_file"

  if compose --progress plain build "$service" > "$log_file" 2>&1; then
    echo "ok: $service"
  else
    save_resume_state "$service"
    print_failure "$service" "$log_file"
    exit 1
  fi
}

show_logs_snapshot() {
  local title="$1"
  shift
  local services=("$@")
  local log_file="$LOG_DIR/logs-$title.log"
  mkdir -p "$LOG_DIR"

  say "Logs: $title"
  echo "saving: $log_file"
  compose logs --tail "$STARTUP_TAIL_LINES" --timestamps "${services[@]}" > "$log_file" 2>&1 || true

  echo "------------------------------------------------------------"
  cat "$log_file"
  echo "------------------------------------------------------------"
  echo "Full log: $log_file"
}

start_services() {
  local services=("$@")
  if [ "${#services[@]}" -eq 0 ]; then
    say "Starting stack without rebuild"
    compose up -d --no-build
  else
    say "Starting selected services without rebuild: ${services[*]}"
    compose up -d --no-build "${services[@]}"
  fi
}

build_targets() {
  ensure_env
  local targets=()
  mapfile -t targets < <(expand_targets "$@")

  CURRENT_TARGETS=("${targets[@]}")
  if [ "$#" -eq 0 ] || [ "${1:-}" = "all" ]; then
    CURRENT_BUILD_MODE="full"
  else
    CURRENT_BUILD_MODE="selected"
  fi

  say "Build logs directory: $LOG_DIR"
  local service
  for service in "${targets[@]}"; do
    build_one "$service"
  done

  clear_resume_state
  say "Build OK"

  if [ "$CURRENT_BUILD_MODE" = "full" ]; then
    start_services
    sleep "$STARTUP_WAIT_SECONDS"
    compose ps | tee "$LOG_DIR/ps.txt"
    show_logs_snapshot main "${MAIN_LOG_SERVICES[@]}"
    echo ""
    echo "Live logs: ./build.sh logs"
    echo "Judge logs: ./build.sh logs judge"
    echo "E2E check:  ./build.sh e2e"
  else
    start_services "${targets[@]}"
    sleep 2
    compose ps "${targets[@]}" | tee "$LOG_DIR/ps-selected.txt" || true
    show_logs_snapshot selected "${targets[@]}"
    echo ""
    echo "Live logs for these services: ./build.sh logs ${targets[*]}"
  fi
}

resume_command() {
  ensure_env
  if [ ! -f "$STATE_DIR/failed-service" ] || [ ! -f "$STATE_DIR/targets" ]; then
    echo "No failed build to resume. Run ./build.sh first."
    return 1
  fi

  local failed_service mode
  failed_service="$(cat "$STATE_DIR/failed-service")"
  mode="$(cat "$STATE_DIR/mode" 2>/dev/null || echo selected)"

  local saved_targets=()
  mapfile -t saved_targets < "$STATE_DIR/targets"

  local resume_targets=()
  local include=0
  local service
  for service in "${saved_targets[@]}"; do
    if [ "$service" = "$failed_service" ]; then
      include=1
    fi
    if [ "$include" -eq 1 ]; then
      resume_targets+=("$service")
    fi
  done

  if [ "${#resume_targets[@]}" -eq 0 ]; then
    echo "Could not find failed service '$failed_service' in saved build list."
    return 1
  fi

  CURRENT_TARGETS=("${saved_targets[@]}")
  CURRENT_BUILD_MODE="$mode"

  say "Resuming from: $failed_service"
  say "Build logs directory: $LOG_DIR"
  for service in "${resume_targets[@]}"; do
    build_one "$service"
  done

  clear_resume_state
  say "Build OK"

  if [ "$CURRENT_BUILD_MODE" = "full" ]; then
    start_services
    sleep "$STARTUP_WAIT_SECONDS"
    compose ps | tee "$LOG_DIR/ps.txt"
    show_logs_snapshot main "${MAIN_LOG_SERVICES[@]}"
    echo ""
    echo "Live logs: ./build.sh logs"
    echo "Judge logs: ./build.sh logs judge"
    echo "E2E check:  ./build.sh e2e"
  else
    start_services "${resume_targets[@]}"
    sleep 2
    compose ps "${resume_targets[@]}" | tee "$LOG_DIR/ps-selected.txt" || true
    show_logs_snapshot selected "${resume_targets[@]}"
    echo ""
    echo "Live logs for these services: ./build.sh logs ${resume_targets[*]}"
  fi
}

logs_command() {
  ensure_env
  shift || true
  local targets=()
  if [ "$#" -eq 0 ]; then
    targets=("${MAIN_LOG_SERVICES[@]}")
  else
    mapfile -t targets < <(expand_targets "$@")
  fi
  compose logs -f --tail "${TASKFORGE_LOG_TAIL:-180}" --timestamps "${targets[@]}"
}

clean_command() {
  say "Cleaning local build artifacts"
  find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
  find . -type d \( -name __pycache__ -o -name .pytest_cache \) -prune -exec rm -rf {} +
  rm -rf deploy/dev/build-logs deploy/dev/.build-state
  echo "ok"
}

cmd="${1:-}"
case "$cmd" in
  -h|--help|help)
    usage
    ;;
  logs)
    logs_command "$@"
    ;;
  resume)
    resume_command
    ;;
  up)
    ensure_env
    shift || true
    start_services "$@"
    ;;
  down)
    ensure_env
    shift || true
    compose down --remove-orphans "$@"
    ;;
  ps)
    ensure_env
    compose ps
    ;;
  clean)
    clean_command
    ;;
  e2e)
    ensure_env
    ./scripts/dev-judge-e2e.sh
    ;;
  *)
    build_targets "$@"
    ;;
esac
