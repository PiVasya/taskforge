#!/usr/bin/env bash
set -Eeuo pipefail

config="${1:-/run/taskforge-cluster/patroni.yml}"
[ -r "$config" ] || { echo "error: Patroni config is missing: $config" >&2; exit 2; }

mkdir -p /var/lib/postgresql /var/run/postgresql
chown -R postgres:postgres /var/lib/postgresql /var/run/postgresql
chmod 2775 /var/run/postgresql

export PATH="/opt/patroni/bin:/usr/lib/postgresql/18/bin:$PATH"
export MALLOC_ARENA_MAX="${MALLOC_ARENA_MAX:-1}"
export PG_MALLOC_ARENA_MAX="${PG_MALLOC_ARENA_MAX-}"

exec gosu postgres /opt/patroni/bin/patroni "$config"
