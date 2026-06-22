#!/usr/bin/env sh
set -eu

base_url="${1:-http://127.0.0.1}"

printf 'Checking %s/ha/live\n' "$base_url"
curl -fsS "$base_url/ha/live"
printf '\n\nChecking %s/ha/primary-ready\n' "$base_url"
curl -i -sS "$base_url/ha/primary-ready"
printf '\n'
