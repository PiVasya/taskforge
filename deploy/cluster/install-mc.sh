#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"

mkdir -p "$TASKFORGE_ROOT/.runtime/cluster/bin"
out="$TASKFORGE_ROOT/.runtime/cluster/bin/mc"

# Keep the client in the same release family as the pinned MinIO server image.
# A floating `latest` download can silently change command/output behaviour and
# make failback checks unreliable.
default_version="RELEASE.2025-04-08T15-39-49Z"
default_url="https://dl.min.io/client/mc/release/linux-amd64/archive/mc.${default_version}"
default_sha256="43d44d801a936e48390c8d2dc02c1c1cc1e973becbe71897f28ca06a9f456489"
url="${MINIO_MC_DOWNLOAD_URL:-$default_url}"
expected_sha256="${MINIO_MC_SHA256:-$default_sha256}"

cluster_need_command curl
cluster_need_command sha256sum

needs_download=1
if [ -x "$out" ]; then
  actual="$(sha256sum "$out" | awk '{print $1}')"
  if [ "$actual" = "$expected_sha256" ]; then
    needs_download=0
  else
    cluster_warn "existing mc checksum differs from the pinned cluster client; replacing it"
  fi
fi

if [ "$needs_download" -eq 1 ]; then
  tmp="$out.tmp.$$"
  trap 'rm -f "$tmp"' EXIT
  curl -fL --retry 3 --connect-timeout 10 "$url" -o "$tmp"
  actual="$(sha256sum "$tmp" | awk '{print $1}')"
  [ "$actual" = "$expected_sha256" ] || cluster_die "mc checksum mismatch: expected $expected_sha256, got $actual"
  chmod 700 "$tmp"
  mv "$tmp" "$out"
  trap - EXIT
fi

"$out" --version
