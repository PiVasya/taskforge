FROM nginx:alpine

# Установим envsubst
RUN apk add --no-cache gettext

# Шаблоны
COPY bootstrap.conf /etc/nginx/templates/bootstrap.conf
COPY https.conf     /etc/nginx/templates/https.conf

# Стартовый скрипт
COPY docker-entrypoint.sh /docker-entrypoint.d/99-select-config.sh
RUN chmod +x /docker-entrypoint.d/99-select-config.sh
