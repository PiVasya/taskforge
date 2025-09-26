FROM nginx:alpine

# Tools: gettext для envsubst; inotify-tools — чтобы перезагружать nginx при обновлении сертификатов
RUN apk add --no-cache gettext inotify-tools

# Шаблоны и скрипты
COPY templates/bootstrap.conf /etc/nginx/templates/bootstrap.conf
COPY templates/https.conf     /etc/nginx/templates/https.conf
COPY 99-select-config.sh      /docker-entrypoint.d/99-select-config.sh
COPY 10-watch-certs.sh        /docker-entrypoint.d/10-watch-certs.sh

RUN chmod +x /docker-entrypoint.d/99-select-config.sh \
    && chmod +x /docker-entrypoint.d/10-watch-certs.sh
