#!/usr/bin/env bash
set -Eeuo pipefail

# Fail closed if this kubeconfig points outside the dedicated LOCAL K3s.
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/guard-local-context.sh"
# Build the exact patched sources locally, then import into K3s containerd.
# We DO NOT use stale develop images, and never push to GHCR.
ROOT="${1:-}"
PROFILE="${2:-core}"
if [[ -z "$ROOT" || ! -f "$ROOT/00_AI_READ_THIS_FIRST.md" ]]; then
  echo 'Usage: build-images.sh /path/to/ALREADY_PATCHED/taskforge [core|all-api]' >&2; exit 2
fi
[[ "$PROFILE" == core || "$PROFILE" == all-api ]] || { echo 'Invalid profile' >&2; exit 2; }
for cmd in docker k3s; do command -v "$cmd" >/dev/null || { echo "Missing $cmd" >&2; exit 2; }; done
docker info >/dev/null || { echo 'Docker daemon unavailable' >&2; exit 2; }
ROOT="$(cd "$ROOT" && pwd)"
PREFIX='ghcr.io/pivasya/taskforge-k3s-lab'
TAG='lab-local-v1'
images=(identity-api education-api content-api tasks-api solutions-api execution-api)
if [[ "$PROFILE" == all-api ]]; then
  images+=(ai-api support-api minecraft-api files-api notifications-api observability-api)
fi
images+=(front)
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
build(){
  local name="$1" dockerfile context image
  case "$name" in
    identity-api) dockerfile=services/identity/api/Dockerfile; context=. ;;
    education-api) dockerfile=services/education/api/Dockerfile; context=. ;;
    content-api) dockerfile=services/content/api/Dockerfile; context=services/content/api ;;
    tasks-api) dockerfile=services/tasks/assignment-api/Dockerfile; context=. ;;
    solutions-api) dockerfile=services/solutions/api/Dockerfile; context=. ;;
    execution-api) dockerfile=services/execution/api/Dockerfile; context=. ;;
    ai-api) dockerfile=services/ai/api/Dockerfile; context=. ;;
    support-api) dockerfile=services/support/api/Dockerfile; context=. ;;
    minecraft-api) dockerfile=services/minecraft/api/Dockerfile; context=. ;;
    files-api) dockerfile=services/files/api/Dockerfile; context=. ;;
    notifications-api) dockerfile=services/notifications/api/Dockerfile; context=. ;;
    observability-api) dockerfile=services/observability/api/Dockerfile; context=. ;;
    front) dockerfile=apps/web/Dockerfile; context=apps/web ;;
    *) echo "Unknown image: $name" >&2; exit 2 ;;
  esac
  image="$PREFIX/$name:$TAG"
  echo "== BUILD $image =="
  (cd "$ROOT" && docker build -f "$dockerfile" -t "$image" "$context")
  docker save -o "$TMP/$name.tar" "$image"
  sudo k3s ctr -n k8s.io images import "$TMP/$name.tar"
  rm -f "$TMP/$name.tar"
}
for name in "${images[@]}"; do build "$name"; done
echo 'All selected images are available in the local K3s containerd namespace k8s.io.'
