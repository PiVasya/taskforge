FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci --no-audit --no-fund

COPY . .
ARG REACT_APP_API_BASE=/api
ARG TASKFORGE_PUBLIC_SOURCEMAPS=0
ENV REACT_APP_API_BASE=$REACT_APP_API_BASE
RUN if [ "$TASKFORGE_PUBLIC_SOURCEMAPS" = "1" ]; then \
      echo "Building frontend with public source maps"; \
      GENERATE_SOURCEMAP=true npm run build; \
    else \
      echo "Building frontend without public source maps"; \
      GENERATE_SOURCEMAP=false npm run build; \
      find build -type f -name '*.map' -delete; \
    fi

FROM caddy:2-alpine
COPY --from=build /app/build /usr/share/caddy
COPY Caddyfile /etc/caddy/Caddyfile

EXPOSE 80
