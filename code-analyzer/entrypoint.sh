#!/bin/sh
set -eu

echo "[code-analyzer] entrypoint: starting"
echo "[code-analyzer] entrypoint: uid=$(id -u) gid=$(id -g)"
echo "[code-analyzer] entrypoint: RUST_LOG=${RUST_LOG:-} RUST_BACKTRACE=${RUST_BACKTRACE:-}"
echo "[code-analyzer] entrypoint: cmd=/app/taskforge-code-analyzer"

echo "[code-analyzer] entrypoint: launching..."
/app/taskforge-code-analyzer
rc=$?

echo "[code-analyzer] entrypoint: process exited with code=$rc"
# Keep container alive briefly so logs are visible in case of crash-loop.
sleep 2
exit $rc
