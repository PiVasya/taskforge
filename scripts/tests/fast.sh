#!/usr/bin/env bash
# Isolated K3s-only branch. Legacy Compose fallback was retired.
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
exec bash "$ROOT/scripts/tests/fast-k3s.sh" "$@"
