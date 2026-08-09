#!/bin/sh
set -eu

LOG_DIR=/var/log/taskforge-ai
LOG_FILE="${LOG_DIR}/edge-events.log"
RETENTION_SECONDS="${GATEWAY_AI_TELEMETRY_LOG_RETENTION_SECONDS:-21600}"

case "$RETENTION_SECONDS" in
  ''|*[!0-9]*) RETENTION_SECONDS=21600 ;;
esac
if [ "$RETENTION_SECONDS" -lt 300 ]; then RETENTION_SECONDS=300; fi

mkdir -p "$LOG_DIR"
touch "$LOG_FILE"

# This file is a short delivery bridge from nginx to support-bot, not an audit log.
# Keep it bounded in time and truncate in-place so nginx's open file descriptor
# remains valid and the support-bot tailer can detect/reopen after truncation.
(
  while true; do
    sleep "$RETENTION_SECONDS"
    : > "$LOG_FILE"
  done
) &
