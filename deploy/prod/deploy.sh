#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

scripts/prod/prepare-env.sh
scripts/prod/check-prod-config.sh

# First launch pulls missing images. Regular updates are handled by Watchtower,
# so deploy.sh does not pull every image on every run.
./deploy/prod/compose.sh up-logs --pull missing

echo ""
echo "TaskForge production stack started."
echo "Watchtower will keep labeled TaskForge containers updated from GHCR."
echo "Next commands:"
echo "  ./deploy/prod/compose.sh ps"
echo "  ./deploy/prod/compose.sh logs -f --tail=200"
echo "  ./deploy/prod/compose.sh logs -f watchtower"
