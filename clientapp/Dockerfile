# build
FROM node:20 AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# важно: пускай фронт ходит к /api, а не к абсолютному домену
ENV REACT_APP_API_BASE=/api
RUN npm run build

# runtime
FROM caddy:2-alpine
# Caddy сам отдаёт всё из /usr/share/caddy
COPY --from=build /app/build /usr/share/caddy
# базовый Caddyfile подходит по умолчанию (root=/usr/share/caddy, index index.html)
EXPOSE 80
