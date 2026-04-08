#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "[1/4] Python compile check"
python3 -m py_compile taskforge-ai-worker/*.py taskforge-ai-worker-external/*.py

echo "[2/4] Worker contract tests"
python3 taskforge-ai-worker/tests/test_contracts.py
python3 taskforge-ai-worker-external/tests/test_contracts.py

echo "[3/4] Frontend install"
cd clientapp
npm ci --no-audit --no-fund

echo "[4/4] Frontend production build"
npm run build

echo "OK: available checks passed"
