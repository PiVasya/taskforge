#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/scripts/sql/engine-gate-readiness.sh"

command -v docker >/dev/null || { echo 'Docker is required for the real SQL engine/RabbitMQ gate.' >&2; exit 2; }
docker info >/dev/null || { echo 'Docker daemon is not available.' >&2; exit 2; }
command -v go >/dev/null || { echo 'Go is required for the real SQL engine/RabbitMQ gate.' >&2; exit 2; }
command -v gcc >/dev/null || { echo 'gcc is required for the race-enabled SQL engine gate.' >&2; exit 2; }
command -v pkg-config >/dev/null || { echo 'pkg-config is required for the SQL native libraries.' >&2; exit 2; }
pkg-config --exists libpq libmariadb sqlite3 || { echo 'Install libpq, MariaDB Connector/C and SQLite development packages.' >&2; exit 2; }

suffix="$(date +%s)-$$"
pg="tf-sql-check-pg-$suffix"
my="tf-sql-check-my-$suffix"
rabbit="tf-sql-check-rabbit-$suffix"
tmp="$(mktemp -d)"

container_exists() {
  docker inspect "$1" >/dev/null 2>&1
}

published_port() {
  local container="$1" container_port="$2" value
  value="$(docker port "$container" "$container_port/tcp" 2>/dev/null | tail -n 1)"
  value="${value##*:}"
  [[ "$value" =~ ^[0-9]+$ ]] || return 1
  printf '%s\n' "$value"
}

print_failure_diagnostics() {
  echo >&2
  echo '========== SQL ENGINE GATE FAILURE DIAGNOSTICS ==========' >&2
  for c in "$pg" "$my" "$rabbit"; do
    if ! container_exists "$c"; then
      continue
    fi
    echo >&2
    echo "===== $c state =====" >&2
    docker inspect "$c" --format 'status={{.State.Status}} running={{.State.Running}} oom_killed={{.State.OOMKilled}} exit_code={{.State.ExitCode}} error={{json .State.Error}} started={{.State.StartedAt}} finished={{.State.FinishedAt}}' >&2 2>/dev/null || true
    echo "===== $c pid1 =====" >&2
    docker exec "$c" sh -c 'cat /proc/1/comm 2>/dev/null || true' >&2 2>/dev/null || true
    echo "===== $c logs (last 120 lines) =====" >&2
    docker logs --tail 120 "$c" >&2 2>&1 || true
  done
  echo '========== END SQL ENGINE GATE FAILURE DIAGNOSTICS ==========' >&2
}

cleanup() {
  rc=$?
  trap - EXIT
  set +e
  if [ "$rc" -ne 0 ]; then
    print_failure_diagnostics
  fi
  docker rm -f "$pg" "$my" "$rabbit" >/dev/null 2>&1 || true
  rm -rf "$tmp"
  exit "$rc"
}
trap cleanup EXIT

marker="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
password="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
pgimage="${SQL_TEST_POSTGRES_IMAGE:-postgres:18-bookworm}"
myimage="${SQL_TEST_MYSQL_IMAGE:-mysql@sha256:3466ba4a4828aa8d46fb7c3bc16b67b781c98413cf4ea0fac6feaa6e881faa26}"
rabbitimage="${SQL_TEST_RABBIT_IMAGE:-rabbitmq:4-management}"

# These are disposable provider images only. The TaskForge sql-worker image is
# deliberately NOT built here: production image building belongs to the image
# matrix and happens once, only when sql-worker sources changed.
docker pull -q "$pgimage" >/dev/null
docker pull -q "$myimage" >/dev/null
docker pull -q "$rabbitimage" >/dev/null
pgdigest="$(docker image inspect "$pgimage" --format '{{index .RepoDigests 0}}')"; pgdigest="${pgdigest##*@}"
mydigest="$(docker image inspect "$myimage" --format '{{index .RepoDigests 0}}')"; mydigest="${mydigest##*@}"
[[ "$pgdigest" =~ ^sha256:[a-f0-9]{64}$ && "$mydigest" =~ ^sha256:[a-f0-9]{64}$ ]] || { echo 'Missing immutable engine digests.' >&2; exit 1; }

# Bind only random loopback ports. These are throw-away test databases; no
# production database, Patroni network, host DB directory or persistent volume is used.
docker run -d --name "$pg" -p 127.0.0.1::5432 \
  --user 999:999 --read-only --cap-drop ALL --security-opt no-new-privileges:true \
  --memory 768m --memory-swap 768m --cpus 1 --pids-limit 128 \
  --tmpfs /var/lib/postgresql:rw,size=384m,uid=999,gid=999,mode=0700 \
  --tmpfs /var/run/postgresql:rw,size=8m,uid=999,gid=999,mode=0770 --tmpfs /tmp:rw,size=32m \
  -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$password" -e SQL_SANDBOX_MARKER="$marker" \
  -v "$ROOT/infrastructure/sql/postgres-init:/docker-entrypoint-initdb.d:ro" "$pgimage" \
  -c shared_buffers=64MB -c max_connections=32 -c fsync=off -c full_page_writes=off \
  -c log_min_error_statement=panic -c log_error_verbosity=terse >/dev/null

docker run -d --name "$my" -p 127.0.0.1::3306 \
  --read-only --cap-drop ALL --security-opt no-new-privileges:true \
  --memory 1g --memory-swap 1g --cpus 1 --pids-limit 192 \
  --tmpfs /var/lib/mysql:rw,size=512m,uid=999,gid=999,mode=0700 \
  --tmpfs /var/run/mysqld:rw,size=8m,uid=999,gid=999,mode=0770 --tmpfs /tmp:rw,size=32m \
  -e MYSQL_ROOT_PASSWORD="$password" -e MYSQL_ROOT_HOST=% -e SQL_SANDBOX_MARKER="$marker" \
  -v "$ROOT/infrastructure/sql/mysql-init:/docker-entrypoint-initdb.d:ro" "$myimage" \
  --innodb-buffer-pool-size=134217728 --innodb-redo-log-capacity=67108864 --performance-schema=OFF \
  --skip-log-bin --mysqlx=OFF --local-infile=OFF --secure-file-priv=NULL --max-connections=32 >/dev/null

docker run -d --name "$rabbit" -p 127.0.0.1::5672 -p 127.0.0.1::15672 \
  --memory 512m --cpus 1 --pids-limit 128 --tmpfs /var/lib/rabbitmq:rw,size=128m,uid=999,gid=999 \
  -e RABBITMQ_DEFAULT_USER=taskforge -e RABBITMQ_DEFAULT_PASS="$password" \
  -e 'RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS=+S 2:2 +A 4' "$rabbitimage" >/dev/null

# Do not accept the temporary database server used by the official entrypoints
# during first-time initialization. Require the final PID 1 daemon and three
# consecutive successful marker probes.
ready=false
stable=0
for _ in $(seq 1 180); do
  pg_pid1="$(docker exec "$pg" sh -c 'cat /proc/1/comm' 2>/dev/null | tr -d '\r\n' || true)"
  my_pid1="$(docker exec "$my" sh -c 'cat /proc/1/comm' 2>/dev/null | tr -d '\r\n' || true)"
  pg_marker="$(docker exec -e PGPASSWORD="$password" "$pg" psql -h 127.0.0.1 -U postgres -d postgres -Atc 'SELECT token FROM taskforge_sql_guard.runtime WHERE singleton=1' 2>/dev/null || true)"
  my_marker="$(docker exec -e MYSQL_PWD="$password" "$my" mysql --protocol=tcp -h 127.0.0.1 -uroot -Nse 'SELECT token FROM taskforge_sql_guard.runtime WHERE singleton=1' 2>/dev/null || true)"

  if sql_gate_final_daemons_ready "$pg_pid1" "$my_pid1" "$pg_marker" "$my_marker" "$marker" && \
     docker exec "$rabbit" rabbitmq-diagnostics -q check_running >/dev/null 2>&1 && \
     docker exec "$rabbit" rabbitmq-diagnostics -q check_port_connectivity >/dev/null 2>&1; then
    stable=$((stable + 1))
    if [ "$stable" -ge 3 ]; then
      ready=true
      break
    fi
  else
    stable=0
  fi
  sleep 1
done
[ "$ready" = true ] || { echo 'Dedicated SQL test engines or RabbitMQ failed to reach stable final-daemon readiness.' >&2; exit 1; }
echo 'SQL provider gate: final PostgreSQL/MySQL daemons confirmed stable (3/3 probes).'

pgport="$(published_port "$pg" 5432)"
myport="$(published_port "$my" 3306)"
rabbit_amqp_port="$(published_port "$rabbit" 5672)"
rabbit_http_port="$(published_port "$rabbit" 15672)"

worker="$tmp/sql-worker"
(
  cd "$ROOT/services/execution/sql-worker"
  go build -trimpath -buildvcs=false -ldflags='-s -w' -o "$worker" ./cmd/sql-worker
)
"$worker" version
"$worker" self-test-isolation

common_env=(
  SQL_TEST_ENGINE_GATE=1
  SQL_TEST_HELPER="$worker"
  SQL_TEST_PASSWORD="$password"
  SQL_TEST_MARKER="$marker"
  SQL_TEST_PG_DIGEST="$pgdigest"
  SQL_TEST_MY_DIGEST="$mydigest"
  SQL_TEST_PG_HOST=127.0.0.1
  SQL_TEST_PG_PORT="$pgport"
  SQL_TEST_MY_HOST=127.0.0.1
  SQL_TEST_MY_PORT="$myport"
)

(
  cd "$ROOT/services/execution/sql-worker"
  env "${common_env[@]}" go test -race -count=1 -v -timeout=10m -run '^TestRealEngine' ./internal/sqlworker
  env "${common_env[@]}" \
    SQL_TEST_RABBIT_HOST=127.0.0.1 \
    SQL_TEST_RABBIT_AMQP_PORT="$rabbit_amqp_port" \
    SQL_TEST_RABBIT_HTTP_PORT="$rabbit_http_port" \
    go test -race -count=1 -v -timeout=2m -run '^TestRealRabbitWakeup$' ./internal/wakeup
)

echo 'PASS: host Go worker against disposable PostgreSQL 18, MySQL 8.4 and RabbitMQ providers'
echo 'No TaskForge Docker image was built by this test. No production DB was used.'
