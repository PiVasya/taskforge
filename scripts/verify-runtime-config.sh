#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

fail(){ echo "error: $*" >&2; exit 1; }
check_file(){ [ -f "$1" ] || fail "missing file: $1"; }

for file in \
  deploy/dev/compose/00-storage.yaml deploy/prod/compose/00-storage.yaml \
  deploy/dev/compose/30-execution.yaml deploy/prod/compose/30-execution.yaml \
  deploy/dev/compose/40-ai-and-analyzers.yaml deploy/prod/compose/40-ai-and-analyzers.yaml; do
  check_file "$file"
done

if grep -R "/var/lib/postgresql/data" deploy/dev/compose deploy/prod/compose >/dev/null 2>&1; then
  grep -R "/var/lib/postgresql/data" -n deploy/dev/compose deploy/prod/compose >&2 || true
  fail "PostgreSQL 18 must mount postgres-data to /var/lib/postgresql"
fi
for env in dev prod; do
  storage="deploy/$env/compose/00-storage.yaml"
  grep -q "postgres-data:/var/lib/postgresql" "$storage" || fail "$storage has the wrong PostgreSQL volume target"
  grep -q "image: .*postgres:18-alpine" "$storage" || fail "$storage must default to postgres:18-alpine"
  grep -q "../../../infrastructure/postgres/init:/docker-entrypoint-initdb.d:ro" "$storage" \
    || fail "$storage has the wrong init mount path"
done

runners=(
  csharp-runner cpp-runner java-runner javascript-runner pascal-runner python-runner
  image-cpp-runner image-pascal-runner image-python-runner
)

service_block() {
  local file="$1" service="$2"
  awk -v service="$service" '
    $0 == "  " service ":" {inside=1; print; next}
    inside && /^  [A-Za-z0-9_-]+:/ {exit}
    inside {print}
  ' "$file"
}

network_block() {
  local file="$1" network="$2"
  awk -v network="$network" '
    $0 == "  " network ":" {inside=1; print; next}
    inside && /^  [A-Za-z0-9_-]+:/ {exit}
    inside {print}
  ' "$file"
}

for env in dev prod; do
  execution="deploy/$env/compose/30-execution.yaml"
  analyzer="deploy/$env/compose/40-ai-and-analyzers.yaml"

  worker="$(service_block "$execution" execution-worker)"
  [ -n "$worker" ] || fail "$execution is missing execution-worker"

  for runner in "${runners[@]}"; do
    block="$(service_block "$execution" "$runner")"
    [ -n "$block" ] || fail "$execution is missing $runner"
    expected_net="${runner}-net"
    printf '%s\n' "$block" | grep -q -- "- $expected_net" || fail "$execution: $runner must join only $expected_net"
    printf '%s\n' "$block" | grep -q 'user: "65534:65534"' || fail "$execution: $runner must run as uid/gid 65534"
    printf '%s\n' "$block" | grep -q 'read_only: true' || fail "$execution: $runner root filesystem must be read-only"
    printf '%s\n' "$block" | grep -q 'CODE_ANALYZER_PUBLIC_KEY_PATH' || fail "$execution: $runner is missing analyzer public-key verification"
    printf '%s\n' "$worker" | grep -q -- "- $expected_net" || fail "$execution: execution-worker must join $expected_net"
    net_block="$(network_block "$execution" "$expected_net")"
    printf '%s\n' "$net_block" | grep -q 'internal: true' || fail "$execution: $expected_net must be internal"
  done

  printf '%s\n' "$worker" | grep -q -- '- code-analyzer-net' || fail "$execution: execution-worker must join code-analyzer-net"
  code_net="$(network_block "$execution" code-analyzer-net)"
  printf '%s\n' "$code_net" | grep -q 'internal: true' || fail "$execution: code-analyzer-net must be internal"

  analyzer_block="$(service_block "$analyzer" code-analyzer)"
  [ -n "$analyzer_block" ] || fail "$analyzer is missing code-analyzer"
  printf '%s\n' "$analyzer_block" | grep -q 'user: "1000:1000"' || fail "$analyzer: code-analyzer must run as uid/gid 1000"
  printf '%s\n' "$analyzer_block" | grep -q 'read_only: true' || fail "$analyzer: code-analyzer root filesystem must be read-only"
  printf '%s\n' "$analyzer_block" | grep -q -- '- code-analyzer-net' || fail "$analyzer: code-analyzer must join code-analyzer-net"
  printf '%s\n' "$analyzer_block" | grep -q 'code_analyzer_private_key' || fail "$analyzer: code-analyzer private key secret is missing"
done

# Telegram bot token boundary: only support-bot may receive or use the bot token.
if grep -R -n -E 'api\.telegram\.org|SUPPORT_BOT_TOKEN|TELEGRAM_BOT_TOKEN|Telegram__BotToken' services/identity/api >/dev/null 2>&1; then
  grep -R -n -E 'api\.telegram\.org|SUPPORT_BOT_TOKEN|TELEGRAM_BOT_TOKEN|Telegram__BotToken' services/identity/api >&2 || true
  fail "identity-api must never access Telegram Bot API or receive the bot token"
fi

for env in dev prod; do
  core="deploy/$env/compose/20-core-services.yaml"
  integrations="deploy/$env/compose/50-integrations.yaml"
  identity_block="$(service_block "$core" identity-api)"
  support_bot_block="$(service_block "$integrations" support-bot)"

  printf '%s\n' "$identity_block" | grep -q 'Services__SupportBot: http://support-bot:8080' \
    || fail "$core: identity-api must deliver recovery messages through support-bot"
  if printf '%s\n' "$identity_block" | grep -Eq 'SUPPORT_BOT_TOKEN|TELEGRAM_BOT_TOKEN|Telegram__BotToken'; then
    fail "$core: Telegram bot token must not be injected into identity-api"
  fi
  printf '%s\n' "$support_bot_block" | grep -q 'Telegram__BotToken:' \
    || fail "$integrations: support-bot must receive the Telegram bot token"
  printf '%s\n' "$support_bot_block" | grep -q 'ASPNETCORE_URLS: http://+:8080' \
    || fail "$integrations: support-bot internal delivery API must listen on port 8080"
  printf '%s\n' "$support_bot_block" | grep -q -- "- '8080'" \
    || fail "$integrations: support-bot internal delivery API must expose port 8080"
done

if [ "${TASKFORGE_PACKAGING_CHECK:-0}" = "1" ] && [ -d deploy/dev/build-logs ]; then
  fail "deploy/dev/build-logs must not be packed into runnable archives"
fi

echo "runtime config ok"
