#!/bin/sh
set -eu

dbs="taskforge_identity taskforge_education taskforge_content taskforge_tasks taskforge_solutions taskforge_execution taskforge_ai taskforge_support taskforge_minecraft taskforge_files taskforge_notifications taskforge_observability taskforge_telegram_quiz"
for db in $dbs; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<SQL
SELECT 'CREATE DATABASE $db'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$db')\gexec
SQL
done
