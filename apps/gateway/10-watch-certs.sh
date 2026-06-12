#!/bin/sh
set -eu

DOMAIN="${DOMAIN:-taskforge.example.com}"
TASKFORGE_DEBUG_LOGS="${TASKFORGE_DEBUG_LOGS:-0}"
TPL_DIR=/etc/nginx/templates
CONF=/etc/nginx/conf.d/default.conf
LIVE_DIR="/etc/letsencrypt/live/${DOMAIN}"

render_https() {
  envsubst '${DOMAIN} ${CT_DOMAIN}' < "${TPL_DIR}/https.conf" > "${CONF}"
  if [ "$TASKFORGE_DEBUG_LOGS" = "1" ]; then
    sed -i '1i error_log /var/log/nginx/error.log info;' "${CONF}"
  fi
}

ensure_https_config () {
  if [ -f "${LIVE_DIR}/fullchain.pem" ] && ! grep -q "listen 443 ssl" "${CONF}" 2>/dev/null; then
    echo "[nginx] Certificate present — switching config to HTTPS"
    render_https
    nginx -s reload || true
  fi
}

(
  # Ждём появления каталога live (первая выдача) и переключаемся на https
  while [ ! -d "${LIVE_DIR}" ]; do sleep 5; done
  ensure_https_config

  if command -v inotifywait >/dev/null 2>&1; then
    inotifywait -m -e close_write,move,create,delete "${LIVE_DIR}" | while read _; do
      ensure_https_config
      echo "[nginx] Certificate files changed — reload"
      nginx -s reload || true
    done
  else
    echo "[nginx] inotifywait is not available; skipping automatic reload on renew"
  fi
) &
