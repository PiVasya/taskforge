FROM nginx:alpine

# envsubst для подстановки ${DOMAIN}
RUN apk add --no-cache gettext

# Кладём шаблоны и скрипт выбора конфига
COPY templates/bootstrap.conf /etc/nginx/templates/bootstrap.conf
COPY templates/https.conf     /etc/nginx/templates/https.conf
COPY 99-select-config.sh      /docker-entrypoint.d/99-select-config.sh

RUN chmod +x /docker-entrypoint.d/99-select-config.sh
