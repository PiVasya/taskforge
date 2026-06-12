FROM node:20-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci --no-audit --no-fund

COPY . .
ARG REACT_APP_API_BASE=/api
ARG TASKFORGE_DEBUG_LOGS=0
ENV REACT_APP_API_BASE=$REACT_APP_API_BASE
ENV REACT_APP_TASKFORGE_DEBUG_LOGS=$TASKFORGE_DEBUG_LOGS
RUN npm run build

FROM caddy:2-alpine
COPY --from=build /app/build /usr/share/caddy
COPY Caddyfile /etc/caddy/Caddyfile

EXPOSE 80
ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=front\"; else echo \"[taskforge-debug] logs=off service=front\"; fi; exec caddy run --config /etc/caddy/Caddyfile --adapter caddyfile"]
