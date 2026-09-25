#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

tmp="$(mktemp -d)"
cleanup() {
  for environment in dev prod; do
    target="deploy/$environment/.env"
    backup="$tmp/$environment.env"
    if [ -f "$backup" ]; then
      cp "$backup" "$target"
    elif [ -f "$tmp/$environment.absent" ]; then
      rm -f "$target"
    fi
  done
  rm -rf "$tmp"
}
trap cleanup EXIT HUP INT TERM

for environment in dev prod; do
  target="deploy/$environment/.env"
  if [ -f "$target" ]; then
    cp "$target" "$tmp/$environment.env"
  else
    : > "$tmp/$environment.absent"
  fi
  cp "deploy/$environment/.env.example" "$target"
  bash "deploy/$environment/compose.sh" config >/dev/null
  echo "PASS: $environment Compose config"
done
