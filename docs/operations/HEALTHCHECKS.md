# Healthchecks

В dev/prod compose healthcheck есть у каждого сервиса.

## Типы healthcheck

- `postgres` — `pg_isready`.
- `rabbitmq` — `rabbitmq-diagnostics ping`.
- `minio` — `/minio/health/live` с fallback на process check.
- `gateway` — `GET /health/gateway`.
- `front`, `front-ct` — `GET /health` из Caddy.
- .NET API — `GET /health/ready`; readiness проверяет подключение к своей БД через `Database.CanConnectAsync()`.
- `content-api`, `quiz-api` — собственный `/health/ready` с проверкой БД.
- code/image analyzers — `GET /health`.
- language runners — `GET /health`.
- background workers / adapter bots — process healthcheck (`kill -0 1`), потому что они не открывают HTTP-порт.
- `certbot` — healthcheck отключён осознанно, потому что это одноразовый профильный job.

## Зависимости

`depends_on` переведён на `condition: service_healthy` там, где сервис реально зависит от другого сервиса:

- API ждут `postgres` и при необходимости `rabbitmq`.
- `files-api` ждёт `minio`.
- `execution-worker` ждёт `rabbitmq` и все runner-сервисы.
- `ai-worker` ждёт `ai-api` и `rabbitmq`.
- `support-bot` ждёт `support-api`.
- `gateway` ждёт front/API сервисы в healthy-состоянии.

## Проверка

```bash
./scripts/verify-structure.sh
```

В скрипте есть отдельная проверка, что у каждого compose-сервиса есть healthcheck.
