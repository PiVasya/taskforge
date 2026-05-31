# Production запуск через split Docker Compose

Этот каталог рассчитан на сценарий: перенёс архив на сервер, создал `deploy/prod/.env`, поменял значения и поднял production.

## Быстрый запуск

```bash
cp deploy/prod/.env.example deploy/prod/.env
# отредактировать deploy/prod/.env

./deploy/prod/compose.sh pull
./deploy/prod/compose.sh up -d
```

`compose.sh` — это тонкая обёртка над `docker compose`, которая подключает split-файлы из `deploy/prod/compose/` в правильном порядке.

## Из чего собрана production-схема

```text
deploy/prod/compose/
  00-storage.yaml          PostgreSQL, RabbitMQ, MinIO
  10-apps-gateway.yaml     gateway, web, web-ct
  20-core-services.yaml    identity, education, content, tasks, quiz, solutions, rating-worker
  30-execution.yaml        execution-api, execution-worker, runners
  40-ai-and-analyzers.yaml ai-api, ai-worker, analyzers
  50-integrations.yaml     support, minecraft, files, notifications, observability, bots
  90-certbot.yaml          optional certbot profile
```

## HTTPS через встроенный nginx + certbot

1. В `.env` укажи реальные `DOMAIN`, `CT_DOMAIN`, `LETSENCRYPT_EMAIL`.
2. Первый запуск делай с `GATEWAY_MODE=http` или `auto`, чтобы webroot был доступен.
3. Выпусти сертификат:

```bash
./deploy/prod/compose.sh --profile certbot run --rm certbot
```

4. Поставь:

```text
GATEWAY_MODE=https
```

5. Перезапусти gateway:

```bash
./deploy/prod/compose.sh up -d gateway
```

## Миграции

Миграции в архиве специально не сгенерированы. Их генерируешь только ты.

Автоприменение миграций оставлено: `MIGRATE_ON_STARTUP=true` по умолчанию. То есть если ты сам добавил EF migrations в сервис, сервис применит их при старте.

Для одного production-сервера через Docker Compose это удобно. Для будущего Kubernetes/нескольких replicas лучше перейти на отдельные migrator jobs, чтобы несколько replicas одного API не пытались одновременно менять одну БД.

## Масштабирование на одном сервере

```bash
./deploy/prod/compose.sh up -d \
  --scale execution-worker=2 \
  --scale ai-worker=2 \
  --scale cpp-runner=2
```

В compose нет `container_name`, поэтому масштабирование не ломается.

## Что хранится постоянно

- `postgres-data` — базы микросервисов.
- `rabbitmq-data` — durable-очереди.
- `minio-data` — файлы, артефакты, картинки.
- `letsencrypt` — TLS-сертификаты.

Перед настоящей продой настрой бэкапы PostgreSQL и MinIO.
## Порядок старта сервисов

В split-compose сервисы, которым нужны PostgreSQL/RabbitMQ, ждут их через `depends_on.condition: service_healthy`. Это особенно важно при `MIGRATE_ON_STARTUP=true`: API не должен пытаться применять EF migrations, пока PostgreSQL ещё принимает bootstrap/init scripts.


## Startup logs

To avoid flooding the terminal, use:

```bash
./deploy/prod/compose.sh up-logs
```

It starts the stack in detached mode and saves the first 30 seconds of logs to `deploy/prod/logs/<timestamp>/startup-30s.log`.
