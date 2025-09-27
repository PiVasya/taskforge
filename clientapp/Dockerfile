# ---------- build ----------
FROM node:20-alpine AS build
WORKDIR /app

# Ставим зависимости
COPY package*.json ./
RUN npm ci --no-audit --no-fund

# Сборка
COPY . .
# База для API в рантайме остаётся /api (за прокси отвечает nginx или Caddy)
ARG REACT_APP_API_BASE=/api
ENV REACT_APP_API_BASE=$REACT_APP_API_BASE
RUN npm run build

# ---------- runtime ----------
FROM caddy:2-alpine
# Статика CRA по умолчанию лежит в /usr/share/caddy
COPY --from=build /app/build /usr/share/caddy
# Конфиг Caddy
COPY Caddyfile /etc/caddy/Caddyfile

EXPOSE 80
