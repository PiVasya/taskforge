#!/bin/sh
set -e

DOMAIN="${DOMAIN:-taskforge.example.com}"

# Если есть сертификат — используем HTTPS-конфиг, иначе — bootstrap (только :80 + challenge)
if [ -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" ]; then
  cp /etc/nginx/templates/https.conf /etc/nginx/conf.d/default.conf
else
  cp /etc/nginx/templates/bootstrap.conf /etc/nginx/conf.d/default.conf
fi
