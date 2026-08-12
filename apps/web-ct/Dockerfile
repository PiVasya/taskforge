FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci --no-audit --no-fund

COPY . .
ARG REACT_APP_API_BASE=/api
ENV REACT_APP_API_BASE=$REACT_APP_API_BASE
RUN npm run build

FROM caddy:2-alpine
COPY --from=build /app/build /usr/share/caddy
COPY Caddyfile /etc/caddy/Caddyfile

EXPOSE 80
