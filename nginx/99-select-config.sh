#!/bin/sh
set -eu

# Берём из .env
DOMAIN="${DOMAIN:-taskforge.example.com}"

render_conf () {
  envsubst '${DOMAIN}' < "/etc/nginx/templates/$1" > /etc/nginx/conf.d/default.conf
}

# Если серт уже есть — https, иначе — bootstrap (только 80 + challenge)
if [ -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" ]; then
  echo "[nginx] Using HTTPS config for ${DOMAIN}"
  render_conf https.conf
else
  echo "[nginx] Using BOOTSTRAP (HTTP) config for ${DOMAIN}"
  render_conf bootstrap.conf
fi
