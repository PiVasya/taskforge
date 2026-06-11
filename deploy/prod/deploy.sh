#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

scripts/prod/prepare-env.sh
scripts/prod/check-prod-config.sh

./deploy/prod/compose.sh pull
./deploy/prod/compose.sh up-logs

echo ""
echo "TaskForge production stack started."
echo "Next commands:"
echo "  ./deploy/prod/compose.sh ps"
echo "  ./deploy/prod/compose.sh logs -f --tail=200"
