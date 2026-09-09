#!/usr/bin/env bash
set -Eeuo pipefail
[[ "${SQL_SANDBOX_MARKER:-}" =~ ^[a-f0-9]{64}$ ]] || { echo 'Dedicated SQL sandbox marker is missing' >&2; exit 1; }
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<SQL
CREATE SCHEMA taskforge_sql_guard AUTHORIZATION "$POSTGRES_USER";
REVOKE ALL ON SCHEMA taskforge_sql_guard FROM PUBLIC;
CREATE TABLE taskforge_sql_guard.runtime(singleton integer PRIMARY KEY CHECK(singleton=1), token text NOT NULL);
INSERT INTO taskforge_sql_guard.runtime VALUES(1,'$SQL_SANDBOX_MARKER');
REVOKE ALL ON taskforge_sql_guard.runtime FROM PUBLIC;
REVOKE CONNECT,TEMPORARY ON DATABASE postgres FROM PUBLIC;
REVOKE CONNECT,TEMPORARY ON DATABASE template1 FROM PUBLIC;
SQL
