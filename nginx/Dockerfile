FROM nginx:alpine

# Кладём оба конфига
COPY bootstrap.conf /etc/nginx/templates/bootstrap.conf
COPY https.conf     /etc/nginx/templates/https.conf

# Стартовый скрипт: выбирает конфиг в зависимости от наличия сертификата
COPY docker-entrypoint.sh /docker-entrypoint.d/99-select-config.sh
RUN chmod +x /docker-entrypoint.d/99-select-config.sh
