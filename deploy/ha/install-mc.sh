#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
mkdir -p "$TASKFORGE_ROOT/.runtime/ha/bin"
out="$TASKFORGE_ROOT/.runtime/ha/bin/mc"
url="${MINIO_MC_DOWNLOAD_URL:-https://dl.min.io/client/mc/release/linux-amd64/mc}"
if [ ! -x "$out" ]; then
  command -v curl >/dev/null 2>&1 || ha_die "curl is required"
  tmp="$out.tmp.$$"
  curl -fL --retry 3 --connect-timeout 10 "$url" -o "$tmp"
  chmod 700 "$tmp"
  mv "$tmp" "$out"
fi
"$out" --version
