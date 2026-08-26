#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
cluster_need_command docker
cluster_runtime_prepare
helper="${TASKFORGE_CLUSTER_HELPER_IMAGE:-alpine:3.21.3}"
copy_volume(){
  local logical="$1" destination="$2" volume
  volume="$(cluster_volume_name "$logical")"
  if ! docker volume inspect "$volume" >/dev/null 2>&1; then
    cluster_warn "Docker volume $volume does not exist; $logical has nothing to migrate"
    return 0
  fi
  mkdir -p "$destination"
  docker run --rm \
    -v "$volume:/source:ro" \
    -v "$destination:/destination" \
    "$helper" sh -ec 'cp -a /source/. /destination/'
  cluster_info "copied $logical -> $destination"
}
copy_volume content-data-protection "$TASKFORGE_CLUSTER_RUNTIME/shared/content-data-protection"
copy_volume quiz-data-protection "$TASKFORGE_CLUSTER_RUNTIME/shared/quiz-data-protection"
find "$TASKFORGE_CLUSTER_RUNTIME/shared" -type d -exec chmod 700 {} + 2>/dev/null || true
find "$TASKFORGE_CLUSTER_RUNTIME/shared" -type f -exec chmod 600 {} + 2>/dev/null || true
