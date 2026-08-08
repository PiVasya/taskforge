#!/usr/bin/env bash
set -euo pipefail

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$ROOT"

PROJECT="services/browser/api/TaskForge.Browser.Api.csproj"
RID="linux-x64"
OUT="$(mktemp -d "${TMPDIR:-/tmp}/taskforge-browser-api-publish.XXXXXX")"
trap 'rm -rf "$OUT"' EXIT

printf '[browser-build] restore %s for %s\n' "$PROJECT" "$RID"
dotnet restore "$PROJECT" --runtime "$RID"

printf '[browser-build] publish %s for %s\n' "$PROJECT" "$RID"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  --no-restore \
  -o "$OUT" \
  /p:DebugType=None \
  /p:DebugSymbols=false

test -f "$OUT/TaskForge.Browser.Api" || {
  echo "Browser API build check failed: self-contained executable was not produced." >&2
  exit 1
}

printf '[browser-build] publish ok\n'
