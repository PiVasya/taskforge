#!/bin/sh
set -e

DOMAIN="${DOMAIN:-taskforge.example.com}"

render_conf () {
  # подставим переменные окружения в шаблон
  envsubst '${DOMAIN}' < "/etc/nginx/templates/$1" > /etc/nginx/conf.d/default.conf
}

if [ -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" ]; then
  render_conf https.conf
else
  render_conf bootstrap.conf
fi
