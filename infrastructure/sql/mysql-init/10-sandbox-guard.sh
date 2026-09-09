#!/usr/bin/env bash
set -Eeuo pipefail
[[ "${SQL_SANDBOX_MARKER:-}" =~ ^[a-f0-9]{64}$ ]] || { echo 'Dedicated SQL sandbox marker is missing' >&2; exit 1; }
MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --protocol=socket --user=root <<SQL
CREATE DATABASE taskforge_sql_guard;
CREATE TABLE taskforge_sql_guard.runtime(singleton integer PRIMARY KEY CHECK(singleton=1), token varchar(64) NOT NULL);
INSERT INTO taskforge_sql_guard.runtime VALUES(1,'$SQL_SANDBOX_MARKER');
SQL
