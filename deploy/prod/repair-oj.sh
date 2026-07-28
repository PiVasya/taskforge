#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

scripts/prod/prepare-env.sh --yes
scripts/prod/check-prod-config.sh

services=(
  code-analyzer
  csharp-runner cpp-runner java-runner javascript-runner pascal-runner python-runner
  image-cpp-runner image-pascal-runner image-python-runner
  execution-worker execution-api
)

./deploy/prod/compose.sh pull "${services[@]}"
./deploy/prod/compose.sh up -d --force-recreate "${services[@]}"
./deploy/prod/compose.sh ps "${services[@]}"

echo
printf '%s\n' 'OJ services were recreated with the current RSA key mounts.'
printf '%s\n' 'Check readiness with:'
printf '%s\n' '  ./deploy/prod/compose.sh logs --tail=200 code-analyzer csharp-runner java-runner python-runner'
