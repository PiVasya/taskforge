#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

printf '\n========== REPOSITORY / RELEASE BOUNDARIES ==========\n'
python3 scripts/ci/check-workflow-integrity.py
python3 scripts/ci/check-docker-build-contexts.py
python3 scripts/ci/check-migration-tooling-safety.py
python3 scripts/ci/check-runtime-config-boundaries.py
bash scripts/ci/check-cluster-ha.sh

printf '\nPASS: repository and release boundary checks passed.\n'
