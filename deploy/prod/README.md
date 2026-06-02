# Production запуск через split Docker Compose

Этот каталог рассчитан на сценарий: перенёс архив на сервер, создал `deploy/prod/.env`, поменял значения и поднял production.

## Домены

Production рассчитан на публичные домены:

```text
taskforge.by
ct.taskforge.by
```

В `.env` можно указать другие домены, но frontend всё равно должен ходить только на same-origin `/api/*` и `/hubs/*`. CORS для обычной работы сайта не нужен: gateway маршрутизирует API внутри Docker-сети.

## Быстрый запуск

```bash
cp deploy/prod/.env.example deploy/prod/.env
# отредактировать deploy/prod/.env

./deploy/prod/compose.sh pull
./deploy/prod/compose.sh up -d
```

`compose.sh` — тонкая обёртка над `docker compose`, которая подключает split-файлы из `deploy/prod/compose/` в правильном порядке.

## HTTPS через встроенный nginx + certbot

1. В `.env` укажи реальные `DOMAIN`, `CT_DOMAIN`, `LETSENCRYPT_EMAIL`.
2. Для первой выдачи сертификата оставь `GATEWAY_MODE=auto`. Gateway стартует в bootstrap/http-режиме, если сертификата ещё нет.
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

Если TLS завершает Cloudflare/внешний балансировщик, можно оставить `GATEWAY_MODE=http`, но внешний прокси обязан передавать Host корректно.

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

## Администратор

Админка не должна выдаваться скрытым правилом "первый пользователь — Admin". В production укажи явно:

```text
BOOTSTRAP_FIRST_USER_IS_ADMIN=false
BOOTSTRAP_ADMIN_EMAILS=admin@taskforge.by
```

Пользователь с email из `BOOTSTRAP_ADMIN_EMAILS` получит роль `Admin` при регистрации.

## Public URLs

В production не должно быть публичных fallback-URL на localhost. Обязательно настрой:

```text
S3_PUBLIC_ENDPOINT=https://s3.taskforge.by
```

или другой реальный публичный URL для файлов.

## Миграции

Миграции хранятся в репозитории по владельцам данных. Текущий baseline: `InitialMicroserviceSchema`.

Новые изменения схемы добавляются безопасным скриптом:

```bash
./scripts/generate-migrations.sh AddMeaningfulSchemaChange
```

Автоприменение миграций оставлено: `MIGRATE_ON_STARTUP=true` по умолчанию. Для одного production-сервера через Docker Compose это допустимо. Для Kubernetes/нескольких replicas лучше перейти на отдельные migrator jobs.

## Startup logs

```bash
./deploy/prod/compose.sh up-logs
```

Команда стартует стек detached и сохраняет первые 30 секунд логов в `deploy/prod/logs/<timestamp>/startup-30s.log`.
