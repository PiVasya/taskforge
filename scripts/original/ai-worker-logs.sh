#!/usr/bin/env bash
set -euo pipefail

WORKER_NAME="${1:-taskforge-ai-worker-external}"
MODE="${2:-text}"
LOG_DIR="${3:-./logs/${WORKER_NAME}}"

TEXT_FILE="${LOG_DIR}/worker.log"
JSON_FILE="${LOG_DIR}/worker.jsonl"

case "$MODE" in
  text)
    exec cat "$TEXT_FILE"
    ;;
  json)
    exec cat "$JSON_FILE"
    ;;
  tail)
    exec tail -n 200 "$TEXT_FILE"
    ;;
  follow)
    exec tail -f "$TEXT_FILE"
    ;;
  docker-text)
    exec docker logs --timestamps --details --tail all "$WORKER_NAME"
    ;;
  docker-file)
    docker logs --timestamps --details --tail all "$WORKER_NAME" > "$TEXT_FILE" 2>&1
    echo "saved to $TEXT_FILE"
    ;;
  *)
    echo "Usage: $0 [worker-name] [text|json|tail|follow|docker-text|docker-file] [log-dir]" >&2
    exit 1
    ;;
esac
