#!/bin/sh
set -e

# Будет подставлен из .env docker-compose
DOMAIN="${DOMAIN:-taskforge.example.com}"

render_conf () {
  envsubst '${DOMAIN}' < "/etc/nginx/templates/$1" > /etc/nginx/conf.d/default.conf
}

# Если сертификат уже есть — сразу https, иначе bootstrap
if [ -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" ]; then
  echo "[nginx] Using HTTPS config for ${DOMAIN}"
  render_conf https.conf
else
  echo "[nginx] Using BOOTSTRAP (HTTP) config for ${DOMAIN}"
  render_conf bootstrap.conf
fi
