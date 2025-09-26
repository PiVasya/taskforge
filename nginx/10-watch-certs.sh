#!/bin/sh
set -eu

DOMAIN="${DOMAIN:-taskforge.example.com}"
LIVE_DIR="/etc/letsencrypt/live/${DOMAIN}"

(
  if command -v inotifywait >/dev/null 2>&1; then
    echo "[nginx] Starting cert watcher for ${LIVE_DIR}"
    # Ждём появления каталога (первая выдача сертификата)
    while [ ! -d "${LIVE_DIR}" ]; do
      sleep 5
    done
    # Следим за изменениями и делаем reload nginx
    inotifywait -m -e close_write,move,create,delete "${LIVE_DIR}" | while read _; do
      echo "[nginx] Certificate changed — reloading nginx"
      nginx -s reload || true
    done
  else
    echo "[nginx] inotifywait is not available; skipping automatic reload on renew"
  fi
) &
