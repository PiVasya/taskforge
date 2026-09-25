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

CODE_ANALYZER_KEY_DIR="${CODE_ANALYZER_KEY_DIR:-$ROOT_DIR/.runtime/code-analyzer-keys}"
if [ ! -s "$CODE_ANALYZER_KEY_DIR/code-analyzer-private.pem" ] || [ ! -s "$CODE_ANALYZER_KEY_DIR/code-analyzer-public.pem" ]; then
  sh "$ROOT_DIR/scripts/security/generate-code-analyzer-keypair.sh" "$CODE_ANALYZER_KEY_DIR"
fi
CODE_ANALYZER_PRIVATE_KEY_PATH="${CODE_ANALYZER_PRIVATE_KEY_PATH:-$CODE_ANALYZER_KEY_DIR/code-analyzer-private.pem}"
CODE_ANALYZER_PUBLIC_KEY_PATH="${CODE_ANALYZER_PUBLIC_KEY_PATH:-$CODE_ANALYZER_KEY_DIR/code-analyzer-public.pem}"
export CODE_ANALYZER_PRIVATE_KEY_PATH CODE_ANALYZER_PUBLIC_KEY_PATH

ENV_FILE="${TASKFORGE_DEV_ENV_FILE:-deploy/dev/.env}"
if [ ! -f "$ENV_FILE" ]; then
  cp deploy/dev/.env.example "$ENV_FILE"
  echo "Created $ENV_FILE"
fi

export TASKFORGE_SQL_INIT_ROOT="$ROOT_DIR/infrastructure/sql"
SQL_RUNTIME_ENV="$ROOT_DIR/.runtime/sql-runtime.env"
case "${1:-}" in
  up|create|start|build|pull)
    python3 "$ROOT_DIR/scripts/sql/prepare-runtime.py" --env-file "$SQL_RUNTIME_ENV" --base-env "$ENV_FILE" --node dev --pull
    ;;
esac
if [ -s "$SQL_RUNTIME_ENV" ]; then
  set -a; . "$SQL_RUNTIME_ENV"; set +a
  if [ "${SQL_ENABLED:-false}" = true ]; then export COMPOSE_PROFILES="${COMPOSE_PROFILES:+$COMPOSE_PROFILES,}sql"; fi
fi

exec docker compose \
  --env-file "$ENV_FILE" \
  -f deploy/dev/compose/00-storage.yaml \
  -f deploy/dev/compose/10-apps-gateway.yaml \
  -f deploy/dev/compose/20-core-services.yaml \
  -f deploy/dev/compose/30-execution.yaml \
  -f deploy/dev/compose/35-sql.yaml \
  -f deploy/dev/compose/40-ai-and-analyzers.yaml \
  -f deploy/dev/compose/50-integrations.yaml \
  "$@"
