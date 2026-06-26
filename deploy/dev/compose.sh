#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
ROOT_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
cd "$ROOT_DIR"

TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR="${TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR:-$ROOT_DIR/.runtime/image-analyzer-model-cache}"
mkdir -p "$TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR" 2>/dev/null || true
chmod 0777 "$TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR" 2>/dev/null || true
export TASKFORGE_IMAGE_ANALYZER_MODEL_CACHE_DIR
export TASKFORGE_ROOT="$ROOT_DIR"

ENV_FILE="${TASKFORGE_DEV_ENV_FILE:-deploy/dev/.env}"
if [ ! -f "$ENV_FILE" ]; then
  cp deploy/dev/.env.example "$ENV_FILE"
  echo "Created $ENV_FILE"
fi

exec docker compose \
  --env-file "$ENV_FILE" \
  -f deploy/dev/compose/00-storage.yaml \
  -f deploy/dev/compose/10-apps-gateway.yaml \
  -f deploy/dev/compose/20-core-services.yaml \
  -f deploy/dev/compose/30-execution.yaml \
  -f deploy/dev/compose/40-ai-and-analyzers.yaml \
  -f deploy/dev/compose/50-integrations.yaml \
  "$@"
