#!/usr/bin/env bash
# Isolated, disposable YugabyteDB smoke test for GitHub Actions.
# Never connects to a production database. All SQL runs against the named CI container.
set -Eeuo pipefail

readonly CONTAINER="${YB_CI_CONTAINER:-taskforge-k3s-lab-ci-db}"
readonly IMAGE="${YB_CI_IMAGE:-yugabytedb/yugabyte:2026.1.2.0-b137}"
readonly HOSTPORT="${YB_CI_PORT:-15433}"
readonly READY_SECONDS="${YB_CI_READY_SECONDS:-420}"
readonly RETRY_SECONDS="${YB_CI_RETRY_SECONDS:-3}"
readonly DIAG_DIR="${RUNNER_TEMP:-/tmp}/taskforge-ysql-diagnostics"

log() { printf '[ysql-ci] %s\n' "$*"; }
fail() { printf '::error title=YugabyteDB CI::%s\n' "$*" >&2; exit 1; }
summary() {
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf '%s\n' "$*" >> "$GITHUB_STEP_SUMMARY"
  fi
}
validate() {
  [[ "$HOSTPORT" =~ ^[0-9]+$ ]] && (( HOSTPORT >= 1024 && HOSTPORT <= 65535 )) || fail 'Invalid local CI port'
  [[ "$READY_SECONDS" =~ ^[0-9]+$ ]] && (( READY_SECONDS >= 1 && READY_SECONDS <= 1200 )) || fail 'Invalid startup deadline'
  [[ "$RETRY_SECONDS" =~ ^[0-9]+$ ]] && (( RETRY_SECONDS >= 1 && RETRY_SECONDS <= 30 )) || fail 'Invalid retry interval'
  [[ "$CONTAINER" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]] || fail 'Invalid isolated container name'
}

# IMPORTANT: Docker yugabyted listens on the container's hostname/IP, not necessarily 127.0.0.1.
# Docker --hostname below makes this predictable; see the official YugabyteDB Docker quick start.
ysql() {
  local database="$1"
  shift
  docker exec "$CONTAINER" bin/ysqlsh \
    --host "$CONTAINER" --port 5433 --username yugabyte --dbname "$database" \
    --set ON_ERROR_STOP=1 "$@"
}

start_database() {
  if docker container inspect "$CONTAINER" >/dev/null 2>&1; then
    fail "Disposable CI container $CONTAINER already exists; refusing to reuse unrelated data"
  fi
  log "Pulling pinned YugabyteDB image: $IMAGE"
  docker pull --quiet "$IMAGE" >/dev/null
  log "Starting disposable RF=1 database (127.0.0.1:$HOSTPORT -> container:5433)"
  docker run --detach --name "$CONTAINER" --hostname "$CONTAINER" \
    --publish "127.0.0.1:${HOSTPORT}:5433" \
    "$IMAGE" bin/yugabyted start \
    --base_dir=/home/yugabyte/yb_data --background=false >/dev/null

  local started_at=$SECONDS next_report=0 last_error='' value='' state=''
  while (( SECONDS - started_at < READY_SECONDS )); do
    state="$(docker inspect --format '{{.State.Status}}' "$CONTAINER" 2>/dev/null || true)"
    if [[ "$state" != 'running' ]]; then
      fail "Database container stopped during startup (state=${state:-missing}); see diagnostic artifact"
    fi
    # Each probe is bounded: no one unresponsive query may hang the job indefinitely.
    if value="$(timeout 10s docker exec "$CONTAINER" bin/ysqlsh \
       --host "$CONTAINER" --port 5433 --username yugabyte --dbname yugabyte \
       --set ON_ERROR_STOP=1 --no-align --tuples-only --quiet --command 'SELECT 1' 2>&1)"; then
      if [[ "$value" =~ ^[[:space:]]*1[[:space:]]*$ ]]; then
        local version
        version="$(ysql yugabyte --no-align --tuples-only --quiet --command 'SELECT version()')" \
          || fail 'YSQL answered SELECT 1 but SELECT version() failed'
        [[ "$version" == *-YB-* ]] || fail "Connected database is not YugabyteDB: $version"
        log "YSQL ready in $(( SECONDS - started_at ))s; server=$CONTAINER; version=$version"
        summary "- **YugabyteDB startup:** ready after $(( SECONDS - started_at ))s; disposable RF=1 Docker instance."
        return 0
      fi
      last_error="Unexpected SELECT 1 response: $value"
    else
      last_error="$value"
    fi
    if (( SECONDS - started_at >= next_report )); then
      log "Waiting for YSQL: elapsed=$(( SECONDS - started_at ))s / ${READY_SECONDS}s; container=$state"
      next_report=$(( next_report + 20 ))
    fi
    sleep "$RETRY_SECONDS"
  done
  log "Last YSQL probe: ${last_error:0:450}"
  fail "YSQL readiness deadline (${READY_SECONDS}s) exceeded. Inspect .State and YB logs in diagnostic artifact"
}

create_databases() {
  local script='infrastructure/k8s/taskforge-yugabyte/db/create-k3s-lab-databases.sql'
  [[ -f "$script" ]] || fail "Missing bootstrap SQL: $script"
  log 'Creating isolated service databases: taskforge_k3s_*'
  docker cp "$script" "$CONTAINER:/tmp/taskforge-create-databases.sql"
  ysql yugabyte --file /tmp/taskforge-create-databases.sql
  summary '- **Database bootstrap:** completed for taskforge_k3s_* databases.'
}

migrate() {
  local entry db project key started_at log_file
  mkdir -p "$DIAG_DIR"
  local entries=(
    'identity services/identity/api/TaskForge.Identity.Api.csproj DefaultConnection'
    'education services/education/api/TaskForge.Education.Api.csproj DefaultConnection'
    'content services/content/api/learning-content-service.csproj LearningConnection'
    'tasks services/tasks/assignment-api/TaskForge.Tasks.Api.csproj DefaultConnection'
    'solutions services/solutions/api/TaskForge.Solutions.Api.csproj DefaultConnection'
    'execution services/execution/api/TaskForge.Execution.Api.csproj DefaultConnection'
  )
  for entry in "${entries[@]}"; do
    read -r db project key <<< "$entry"
    log "Applying EF Core migrations: $db (taskforge_k3s_$db)"
    printf '::group::EF Core | %s\n' "$db"
    started_at=$SECONDS
    log_file="$DIAG_DIR/ef-${db}.log"
    # Preserve full .NET stderr/stdout before the disposable database disappears.
    # pipefail ensures that a failed dotnet run cannot be hidden by successful tee.
    if ( export "ConnectionStrings__${key}=Host=127.0.0.1;Port=${HOSTPORT};Database=taskforge_k3s_${db};Username=yugabyte;SSL Mode=Disable;Timeout=10;Command Timeout=90"; \
         timeout 300s dotnet run --project "$project" --configuration Release --no-launch-profile ) 2>&1 | tee "$log_file"; then
      printf '::endgroup::\n'
      log "EF migrations applied: $db; elapsed=$((SECONDS - started_at))s"
      summary "- **EF Core ($db):** applied in $((SECONDS - started_at))s."
    else
      local rc=$? reason=''
      printf '::endgroup::\n'
      # GitHub collapses the per-service log group. Repeat the underlying error
      # outside that group and retain the complete file in the CI artifact.
      log "EF migration FAILED for $db (exit=$rc); diagnostic excerpt:"
      grep -Ei 'Unhandled exception|Exception:|PostgresException|SQLSTATE|error (CS|NU)[0-9]+|^fail:|^error:' "$log_file" | tail -n 24 || tail -n 28 "$log_file"
      reason="$(grep -Eim1 'Unhandled exception|PostgresException|error (CS|NU)[0-9]+|^fail:|^error:' "$log_file" || true)"
      reason="${reason:0:280}"
      summary "- **EF Core ($db): FAILED (exit $rc).** Full log: \`ef-${db}.log\` in \`taskforge-ysql-diagnostics\`."
      fail "EF migrations failed for $db (exit=$rc). ${reason:-See ef-${db}.log in taskforge-ysql-diagnostics}"
    fi
  done
}

verify() {
  local db name count tables value
  for db in identity education content tasks solutions execution; do
    name="taskforge_k3s_${db}"
    count="$(ysql "$name" --no-align --tuples-only --quiet --command 'SELECT count(*) FROM "__EFMigrationsHistory";')" \
      || fail "Migration history unreadable: $name"
    [[ "$count" =~ ^[1-9][0-9]*$ ]] || fail "Migration history empty: $name ($count)"
    tables="$(ysql "$name" --no-align --tuples-only --quiet \
      --command "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname='public' AND tablename<>'__EFMigrationsHistory';")" \
      || fail "Application tables unreadable: $name"
    [[ "$tables" =~ ^[1-9][0-9]*$ ]] || fail "No application tables in $name"
    # Every service database is separately tested for write/read, not only the bootstrap DB.
    ysql "$name" --quiet --command 'CREATE TABLE IF NOT EXISTS __k3s_ci_write_probe (id int PRIMARY KEY, n int NOT NULL);' >/dev/null
    ysql "$name" --quiet --command 'INSERT INTO __k3s_ci_write_probe(id,n) VALUES(1,1) ON CONFLICT (id) DO UPDATE SET n=EXCLUDED.n;' >/dev/null
    value="$(ysql "$name" --no-align --tuples-only --quiet --command 'SELECT n FROM __k3s_ci_write_probe WHERE id=1;')"
    [[ "$value" == '1' ]] || fail "YSQL write/read verification failed: $name (got $value)"
    log "Verified $name: migrations=$count, tables=$tables, write/read=OK"
    summary "- **$name:** $count migrations, $tables application tables, write/read passed."
  done
}

diagnose() {
  mkdir -p "$DIAG_DIR"
  log "Writing actionable diagnostics to $DIAG_DIR"
  {
    echo '# YugabyteDB / CI diagnostics'
    date -u
    echo 'Container image:' "$IMAGE"
    echo 'Container state:'
    docker inspect --format 'status={{.State.Status}} exit={{.State.ExitCode}} oom_killed={{.State.OOMKilled}} error={{.State.Error}}' "$CONTAINER" 2>&1 || true
    echo 'Container resource usage:'
    docker stats --no-stream --format 'name={{.Name}} cpu={{.CPUPerc}} mem={{.MemUsage}}' "$CONTAINER" 2>&1 || true
    echo 'Docker hostname:'
    docker exec "$CONTAINER" hostname 2>&1 || true
    echo 'Yugabyted status:'
    timeout 10s docker exec "$CONTAINER" bin/yugabyted status --base_dir=/home/yugabyte/yb_data 2>&1 || true
    echo 'Database processes:'
    docker exec "$CONTAINER" sh -c 'ps -ef 2>/dev/null | grep -E "yb-master|yb-tserver|postgres|yugabyted" | head -n 25' 2>&1 || true
    echo 'Yugabyted stdout (progress animation omitted):'
    docker logs --tail 160 "$CONTAINER" 2>&1 \
      | tr '\r' '\n' \
      | grep -v -E 'Starting the YugabyteDB Processes|Checking YSQL Status|Bringing up UI|Verifying data placement constraint' \
      | tail -n 50 || true
    echo 'Recent internal log files:'
    docker exec "$CONTAINER" sh -c 'find /home/yugabyte/yb_data -maxdepth 6 -type f \( -name "*.log" -o -name "*.FATAL" \) 2>/dev/null | head -n 16' 2>&1 || true
  } > "$DIAG_DIR/summary.txt" 2>&1
  cat "$DIAG_DIR/summary.txt"
  # Retrieve full internal logs before the disposable CI container is destroyed.
  docker cp "$CONTAINER:/home/yugabyte/yb_data/logs" "$DIAG_DIR/logs" 2>/dev/null || true
  docker cp "$CONTAINER:/home/yugabyte/yb_data/tserver/logs" "$DIAG_DIR/tserver-logs" 2>/dev/null || true
  docker cp "$CONTAINER:/home/yugabyte/yb_data/master/logs" "$DIAG_DIR/master-logs" 2>/dev/null || true
  summary '- **YugabyteDB:** failed. See `taskforge-ysql-diagnostics` artifact for container state and server logs.'
}

cleanup() {
  if docker container inspect "$CONTAINER" >/dev/null 2>&1; then
    docker rm --force "$CONTAINER" >/dev/null
    log "Disposed of CI YugabyteDB container: $CONTAINER"
  fi
}

validate
case "${1:-}" in
  start) start_database ;;
  create) create_databases ;;
  migrate) migrate ;;
  verify) verify ;;
  diagnose) diagnose ;;
  cleanup) cleanup ;;
  *) printf 'Usage: %s {start|create|migrate|verify|diagnose|cleanup}\n' "$0" >&2; exit 2 ;;
esac
