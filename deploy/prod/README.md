# Production запуск через split Docker Compose

Этот каталог рассчитан на сценарий: перенёс архив на сервер, создал `deploy/prod/.env`, поменял значения и поднял production.

## Домены

Production рассчитан на публичные домены:

```text
taskforge.by
ct.taskforge.by
```

В `.env` можно указать другие домены, но frontend всё равно должен ходить только на same-origin `/api/*` и `/hubs/*`. CORS для обычной работы сайта не нужен: gateway маршрутизирует API внутри Docker-сети.


## One-command deploy

Если `.env` уже настроен, production поднимается одной командой:

```bash
./deploy/prod/deploy.sh
```

Для первого запуска на чистом сервере можно передать публичные значения прямо в команду. Скрипт сам создаст `deploy/prod/.env`, сгенерирует сильные секреты и проверит конфигурацию перед запуском:

```bash
IMAGE_REPOSITORY=ghcr.io/OWNER/REPO \
DOMAIN=taskforge.by \
CT_DOMAIN=ct.taskforge.by \
LETSENCRYPT_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_EMAILS=admin@example.com \
S3_PUBLIC_ENDPOINT=https://s3.taskforge.by \
./deploy/prod/deploy.sh
```

Перед стартом выполняются:

```bash
scripts/prod/prepare-env.sh
scripts/prod/check-prod-config.sh
./deploy/prod/compose.sh config
```

Если в `.env` остались `CHANGE_ME`, слабый JWT/internal-key, `BOOTSTRAP_FIRST_USER_IS_ADMIN=true` или лишние публичные ports, запуск остановится.

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


## Production security checklist

В v18 production-режим должен работать fail-closed:

* `JWT_SIGNING_KEY`, `TASKFORGE_INTERNAL_KEY`, `TASKFORGE_AGENT_INTERNAL_KEY` обязательны и должны быть длинными случайными значениями.
* Пароли пользователей хранятся через PBKDF2-SHA256 с индивидуальной солью. Старые SHA256-хэши читаются только для совместимости и мигрируют при успешном входе.
* `.env` и любые локальные секреты игнорируются `.gitignore`/`.dockerignore`; в репозитории остаются только `.env.example`.
* Internal endpoints принимают только `X-Internal-Key`; слабый/placeholder key в Production отклоняется.
* API с группами, курсами, рейтингом, решениями и runner/compiler endpoints требуют авторизацию или права редактора.
* Runner-контейнеры запускаются с `no-new-privileges`, `cap_drop: ALL`, `read_only: true`, `pids_limit`, `mem_limit`, отдельным internal `runner-net` и tmpfs `/tmp` с `exec` только для компиляции.
* В production наружу публикуется только gateway и localhost-bound storage ports. Остальные сервисы доступны только внутри Docker-сети.

## Startup logs

```bash
./deploy/prod/compose.sh up-logs
```

Команда стартует стек detached и сохраняет первые 30 секунд логов в `deploy/prod/logs/<timestamp>/startup-30s.log`.
