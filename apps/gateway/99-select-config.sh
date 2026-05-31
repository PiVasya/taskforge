#!/bin/sh
set -eu

DOMAIN="${DOMAIN:-taskforge.example.com}"
GATEWAY_MODE="${GATEWAY_MODE:-auto}"

render_conf () {
  envsubst '${DOMAIN} ${CT_DOMAIN}' < "/etc/nginx/templates/$1" > /etc/nginx/conf.d/default.conf
}

if [ "$GATEWAY_MODE" = "dev" ]; then
  echo "[nginx] Using DEV microservices routing"
  render_conf dev.conf
elif [ "$GATEWAY_MODE" = "http" ]; then
  echo "[nginx] Using HTTP production microservices routing"
  render_conf http.conf
elif [ "$GATEWAY_MODE" = "https" ]; then
  echo "[nginx] Using HTTPS production microservices routing"
  render_conf https.conf
elif [ -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" ]; then
  echo "[nginx] Using HTTPS config for ${DOMAIN}"
  render_conf https.conf
else
  echo "[nginx] Using BOOTSTRAP (HTTP) config for ${DOMAIN}"
  render_conf bootstrap.conf
fi
