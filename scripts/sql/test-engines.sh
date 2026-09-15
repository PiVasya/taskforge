#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/scripts/sql/engine-gate-readiness.sh"
command -v docker >/dev/null || { echo 'Docker is required for the real SQL engine/RabbitMQ gate.' >&2; exit 2; }
docker info >/dev/null || { echo 'Docker daemon is not available.' >&2; exit 2; }

suffix="$(date +%s)-$$"
pg="tf-sql-check-pg-$suffix"; my="tf-sql-check-my-$suffix"; rabbit="tf-sql-check-rabbit-$suffix"; test="tf-sql-check-client-$suffix"
pgnet="tf-sql-check-pgnet-$suffix"; mynet="tf-sql-check-mynet-$suffix"; rabbitnet="tf-sql-check-rabbitnet-$suffix"
image="taskforge-sql-check:$suffix"

container_exists() {
  docker inspect "$1" >/dev/null 2>&1
}

print_failure_diagnostics() {
  echo >&2
  echo '========== SQL ENGINE GATE FAILURE DIAGNOSTICS ==========' >&2
  for c in "$pg" "$my" "$rabbit" "$test"; do
    if ! container_exists "$c"; then
      continue
    fi
    echo >&2
    echo "===== $c state =====" >&2
    docker inspect "$c" --format 'status={{.State.Status}} running={{.State.Running}} oom_killed={{.State.OOMKilled}} exit_code={{.State.ExitCode}} error={{json .State.Error}} started={{.State.StartedAt}} finished={{.State.FinishedAt}}' >&2 2>/dev/null || true
    echo "===== $c pid1 =====" >&2
    docker exec "$c" sh -c 'cat /proc/1/comm 2>/dev/null || true' >&2 2>/dev/null || true
    echo "===== $c logs (last 200 lines) =====" >&2
    docker logs --tail 200 "$c" >&2 2>&1 || true
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
  docker rm -f "$test" "$pg" "$my" "$rabbit" >/dev/null 2>&1 || true
  docker network rm "$pgnet" "$mynet" "$rabbitnet" >/dev/null 2>&1 || true
  docker image rm "$image" >/dev/null 2>&1 || true
  exit "$rc"
}
trap cleanup EXIT

marker="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
password="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
pgimage="${SQL_TEST_POSTGRES_IMAGE:-postgres:18-bookworm}"
myimage="${SQL_TEST_MYSQL_IMAGE:-mysql:8.4}"
rabbitimage="${SQL_TEST_RABBIT_IMAGE:-rabbitmq:4-management}"

docker pull "$pgimage"; docker pull "$myimage"; docker pull "$rabbitimage"
pgdigest="$(docker image inspect "$pgimage" --format '{{index .RepoDigests 0}}')"; pgdigest="${pgdigest##*@}"
mydigest="$(docker image inspect "$myimage" --format '{{index .RepoDigests 0}}')"; mydigest="${mydigest##*@}"
[[ "$pgdigest" =~ ^sha256:[a-f0-9]{64}$ && "$mydigest" =~ ^sha256:[a-f0-9]{64}$ ]] || { echo 'Missing immutable engine digests.' >&2; exit 1; }

docker build --target integration --build-arg TASKFORGE_BUILD_DEBUG_LOGS=1 -t "$image" ./services/execution/sql-worker
for network in "$pgnet" "$mynet" "$rabbitnet"; do docker network create --internal "$network" >/dev/null; done

# Long-lived test servers. No application/Patroni DB, host ports or host DB mounts.
docker run -d --name "$pg" --network "$pgnet" --network-alias sql-postgres \
  --user 999:999 --read-only --cap-drop ALL --security-opt no-new-privileges:true \
  --memory 768m --memory-swap 768m --cpus 1 --pids-limit 128 \
  --tmpfs /var/lib/postgresql:rw,size=384m,uid=999,gid=999,mode=0700 \
  --tmpfs /var/run/postgresql:rw,size=8m,uid=999,gid=999,mode=0770 --tmpfs /tmp:rw,size=32m \
  -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$password" -e SQL_SANDBOX_MARKER="$marker" \
  -v "$ROOT/infrastructure/sql/postgres-init:/docker-entrypoint-initdb.d:ro" "$pgimage" \
  -c shared_buffers=64MB -c max_connections=32 -c fsync=off -c full_page_writes=off \
  -c log_min_error_statement=panic -c log_error_verbosity=terse >/dev/null

docker run -d --name "$my" --network "$mynet" --network-alias sql-mysql \
  --user 999:999 --read-only --cap-drop ALL --security-opt no-new-privileges:true \
  --memory 1g --memory-swap 1g --cpus 1 --pids-limit 192 \
  --tmpfs /var/lib/mysql:rw,size=512m,uid=999,gid=999,mode=0700 \
  --tmpfs /var/run/mysqld:rw,size=8m,uid=999,gid=999,mode=0770 --tmpfs /tmp:rw,size=32m \
  -e MYSQL_ROOT_PASSWORD="$password" -e MYSQL_ROOT_HOST=% -e SQL_SANDBOX_MARKER="$marker" \
  -v "$ROOT/infrastructure/sql/mysql-init:/docker-entrypoint-initdb.d:ro" "$myimage" \
  --innodb-buffer-pool-size=134217728 --innodb-redo-log-capacity=67108864 --performance-schema=OFF \
  --skip-log-bin --mysqlx=OFF --local-infile=OFF --secure-file-priv=NULL --max-connections=32 >/dev/null

docker run -d --name "$rabbit" --network "$rabbitnet" --network-alias sql-rabbit \
  --memory 512m --cpus 1 --pids-limit 128 --tmpfs /var/lib/rabbitmq:rw,size=128m,uid=999,gid=999 \
  -e RABBITMQ_DEFAULT_USER=taskforge -e RABBITMQ_DEFAULT_PASS="$password" \
  -e 'RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS=+S 2:2 +A 4' "$rabbitimage" >/dev/null

# IMPORTANT: do not accept the temporary database server used by the official
# PostgreSQL/MySQL entrypoints during first-time initialization. The init server
# can already contain our guard marker and answer SQL, then intentionally shuts
# down before PID 1 execs the final daemon. Starting the integration binary in
# that window produces random 08006/2006 failures in whichever test runs first.
# Require the FINAL daemon to be PID 1 and then require several consecutive
# successful probes before the provider gate is considered ready.
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

docker create --name "$test" --network "$pgnet" --memory 1g --memory-swap 1g --cpus 2 --pids-limit 256 \
  --cap-drop ALL --security-opt no-new-privileges:true --read-only --no-healthcheck \
  --tmpfs /tmp:rw,size=192m,uid=10001,gid=10001,mode=0700,noexec,nosuid,nodev \
  --tmpfs /var/cache/taskforge-sql:rw,size=96m,uid=10001,gid=10001,mode=0700,noexec,nosuid,nodev \
  -e SQL_TEST_ENGINE_GATE=1 -e SQL_TEST_PASSWORD="$password" -e SQL_TEST_MARKER="$marker" \
  -e SQL_TEST_PG_DIGEST="$pgdigest" -e SQL_TEST_MY_DIGEST="$mydigest" -e SQL_TEST_RABBIT_HOST=sql-rabbit \
  "$image" >/dev/null

docker network connect "$mynet" "$test"; docker network connect "$rabbitnet" "$test"
docker start -a "$test"
[ "$(docker inspect "$test" --format '{{.State.ExitCode}}')" = 0 ]
echo 'PASS: actual Go runtime image, PostgreSQL 18, MySQL 8.4, SQLite and RabbitMQ gate'
echo 'Only the current test containers, private networks and image are removed. No production DB was used.'
