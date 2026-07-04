FROM node:20-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm install -g npm@10.9.2 \
    && npm ci --include=dev --no-audit --no-fund \
    && test -x node_modules/.bin/react-scripts

COPY . .
ARG REACT_APP_API_BASE=/api
ENV REACT_APP_API_BASE=$REACT_APP_API_BASE
RUN npm run build

FROM caddy:2-alpine
COPY --from=build /app/build /usr/share/caddy
COPY Caddyfile /etc/caddy/Caddyfile

EXPOSE 80
