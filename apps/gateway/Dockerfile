FROM nginx:alpine
RUN apk add --no-cache gettext inotify-tools
COPY templates/bootstrap.conf /etc/nginx/templates/bootstrap.conf
COPY templates/https.conf     /etc/nginx/templates/https.conf
COPY templates/http.conf      /etc/nginx/templates/http.conf
COPY templates/dev.conf       /etc/nginx/templates/dev.conf
COPY 99-select-config.sh      /docker-entrypoint.d/99-select-config.sh
COPY 10-watch-certs.sh        /docker-entrypoint.d/10-watch-certs.sh
COPY 20-maintain-ai-telemetry-log.sh /docker-entrypoint.d/20-maintain-ai-telemetry-log.sh
COPY snippets/                  /etc/nginx/snippets/
RUN mkdir -p /var/log/taskforge-ai
RUN chmod +x /docker-entrypoint.d/99-select-config.sh \
    && chmod +x /docker-entrypoint.d/10-watch-certs.sh \
    && chmod +x /docker-entrypoint.d/20-maintain-ai-telemetry-log.sh
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
