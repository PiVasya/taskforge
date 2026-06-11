#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${TASKFORGE_PROD_ENV_FILE:-deploy/prod/.env}"
[ -f "$ENV_FILE" ] || { echo "error: missing $ENV_FILE" >&2; exit 2; }

get_env() {
  local key="$1"
  grep -E "^${key}=" "$ENV_FILE" | tail -n1 | cut -d= -f2-
}

fail() { echo "error: $*" >&2; exit 1; }
warn() { echo "warning: $*" >&2; }

require_nonempty() {
  local key="$1"
  local value
  value="$(get_env "$key" || true)"
  [ -n "$value" ] || fail "$key is empty in $ENV_FILE"
}

require_not_placeholder() {
  local key="$1"
  local value
  value="$(get_env "$key" || true)"
  [ -n "$value" ] || fail "$key is empty in $ENV_FILE"
  case "$(printf '%s' "$value" | tr '[:lower:]' '[:upper:]')" in
    *CHANGE_ME*|*DEV_CHANGE_ME*) fail "$key still contains a placeholder" ;;
  esac
}

require_min_len() {
  local key="$1" min="$2"
  local value
  value="$(get_env "$key" || true)"
  [ "${#value}" -ge "$min" ] || fail "$key must be at least $min characters"
}

for key in IMAGE_REPOSITORY DOMAIN CT_DOMAIN LETSENCRYPT_EMAIL POSTGRES_PASSWORD RABBITMQ_DEFAULT_PASS MINIO_ROOT_PASSWORD JWT_SIGNING_KEY TASKFORGE_INTERNAL_KEY TASKFORGE_AGENT_INTERNAL_KEY S3_PUBLIC_ENDPOINT BOOTSTRAP_ADMIN_EMAILS; do
  require_not_placeholder "$key"
done

require_min_len JWT_SIGNING_KEY 64
require_min_len TASKFORGE_INTERNAL_KEY 40
require_min_len TASKFORGE_AGENT_INTERNAL_KEY 40
require_min_len POSTGRES_PASSWORD 24
require_min_len RABBITMQ_DEFAULT_PASS 24
require_min_len MINIO_ROOT_PASSWORD 24

[ "$(get_env BOOTSTRAP_FIRST_USER_IS_ADMIN)" = "false" ] || fail "BOOTSTRAP_FIRST_USER_IS_ADMIN must be false in production"
[ "$(get_env ASPNETCORE_ENVIRONMENT)" = "Production" ] || fail "ASPNETCORE_ENVIRONMENT must be Production"
[ "$(get_env ENSURE_CREATED)" = "false" ] || fail "ENSURE_CREATED must be false in production"

repo="$(get_env IMAGE_REPOSITORY)"
case "$repo" in
  ghcr.io/CHANGE_ME*|*CHANGE_ME*) fail "IMAGE_REPOSITORY must point to your GHCR repository" ;;
esac

domain="$(get_env DOMAIN)"
ct_domain="$(get_env CT_DOMAIN)"
[ "$domain" != "$ct_domain" ] || fail "DOMAIN and CT_DOMAIN must be different"

if grep -R "network_mode:[[:space:]]*host" deploy/prod/compose >/dev/null 2>&1; then
  fail "prod compose must not use host networking"
fi

if grep -R "ports:" deploy/prod/compose/*.yaml | grep -v '00-storage.yaml' | grep -v '10-apps-gateway.yaml' >/dev/null 2>&1; then
  fail "only storage localhost ports and public gateway ports should be published"
fi

if grep -R "127.0.0.1" deploy/prod/compose >/dev/null 2>&1; then
  :
else
  warn "no localhost-bound infra ports found; check prod storage exposure"
fi

./scripts/verify-runtime-config.sh >/dev/null
python3 scripts/ci/check-workflow-integrity.py >/dev/null

if command -v docker >/dev/null 2>&1; then
  ./deploy/prod/compose.sh config >/dev/null
else
  warn "docker is not installed here; skipped docker compose config"
fi

echo "production config ok"
