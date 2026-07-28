# Production запуск TaskForge на Ubuntu

## Live-сервер с каналом develop

Для текущей схемы, где реальные пользователи работают на сервере, а новые изменения
проверяются сразу после сборки, не нужно запускать `deploy/dev`. Используется именно
production runtime из этой папки, но с каналом образов:

```env
IMAGE_TAG=develop
ASPNETCORE_ENVIRONMENT=Production
TASKFORGE_DEBUG_LOGS=1
```

Это сохраняет production-сети, пароли, restart policy, Watchtower и OJ hardening,
но позволяет автоматически получать свежие образы ветки `develop`. Переключение на
стабильный канал позже выполняется одной заменой `IMAGE_TAG`, без переноса volumes.

`prepare-env.sh` синхронизирует старый `.env`, не перезаписывает существующие реальные
секреты, удаляет дубли и создаёт RSA-пару code-analyzer. Для уже инициализированного
сервера он запрещает молча генерировать потерянные пароли хранилищ.

Цель этой папки: скопировал архив/репозиторий на сервер, заполнил `.env`, выполнил одну команду — стек поднялся и дальше обновляется сам через Watchtower.

## Как теперь устроены обновления

GitHub Actions **по умолчанию билдит и пушит только изменённые Docker images**. Если поменялся один микросервис, пересобирается только его image. Все 31 images собираются только при ручном запуске workflow с `build_all=true`.

На сервере работает Watchtower. Он проверяет GHCR, скачивает только images с изменившимся digest и перезапускает только соответствующие контейнеры. PostgreSQL/RabbitMQ/MinIO Watchtower не трогает: обновляются только контейнеры TaskForge с явными labels.

## Первый запуск на чистом Ubuntu-сервере

1. Установить Docker:

```bash
sudo ./scripts/prod/install-ubuntu-docker.sh
```

2. Если GHCR package приватный, залогиниться:

```bash
echo 'GHCR_TOKEN' | docker login ghcr.io -u GITHUB_USERNAME --password-stdin
```

Для public packages этот шаг не нужен.

3. Сгенерировать `.env` и запустить production:

```bash
IMAGE_REPOSITORY=ghcr.io/OWNER/REPO \
IMAGE_TAG=develop \
DOMAIN=taskforge.by \
CT_DOMAIN=ct.taskforge.by \
LETSENCRYPT_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_EMAILS=admin@example.com \
S3_PUBLIC_ENDPOINT=https://s3.taskforge.by \
./deploy/prod/deploy.sh
```

`deploy.sh` сам:

```text
1. создаёт deploy/prod/.env из .env.example;
2. генерирует сильные секреты вместо CHANGE_ME;
3. проверяет production-конфиг;
4. поднимает стек через `up --pull missing`;
5. скачивает только отсутствующие images при первом запуске;
6. включает Watchtower для дальнейших обновлений.
```

После первого запуска `.env` уже существует. Повторный запуск:

```bash
./deploy/prod/deploy.sh
```

## Важные переменные .env

```text
IMAGE_REPOSITORY=ghcr.io/OWNER/REPO
IMAGE_TAG=develop
DOMAIN=taskforge.by
CT_DOMAIN=ct.taskforge.by
LETSENCRYPT_EMAIL=admin@example.com
BOOTSTRAP_ADMIN_EMAILS=admin@example.com
S3_PUBLIC_ENDPOINT=https://s3.taskforge.by
```

Секреты генерируются автоматически:

```text
POSTGRES_PASSWORD
RABBITMQ_DEFAULT_PASS
MINIO_ROOT_PASSWORD
JWT_SIGNING_KEY
TASKFORGE_INTERNAL_KEY
TASKFORGE_AGENT_INTERNAL_KEY
```

Если надо пересгенерировать вручную:

```bash
python3 - <<'PY'
import secrets
for k,n in {
  'POSTGRES_PASSWORD': 36,
  'RABBITMQ_DEFAULT_PASS': 36,
  'MINIO_ROOT_PASSWORD': 36,
  'JWT_SIGNING_KEY': 72,
  'TASKFORGE_INTERNAL_KEY': 48,
  'TASKFORGE_AGENT_INTERNAL_KEY': 48,
}.items():
    print(f'{k}={secrets.token_urlsafe(n)}')
PY
```

## Watchtower

Watchtower включён в production compose:

```text
WATCHTOWER_SCOPE=taskforge-prod
WATCHTOWER_POLL_INTERVAL=300
WATCHTOWER_CLEANUP=true
WATCHTOWER_ROLLING_RESTART=true
```

Проверить логи обновлений:

```bash
./deploy/prod/compose.sh logs -f watchtower
```

Принудительно подтянуть свежие images без ожидания Watchtower:

```bash
./deploy/prod/compose.sh pull
./deploy/prod/compose.sh up -d
```

Обычный повторный запуск `deploy.sh` не делает `pull` всех образов. Он использует `--pull missing`, а обновления после первого запуска выполняет Watchtower.

## HTTPS через gateway + certbot

1. В `.env` указать реальные `DOMAIN`, `CT_DOMAIN`, `LETSENCRYPT_EMAIL`.
2. Для первой выдачи оставить:

```text
GATEWAY_MODE=auto
```

3. Выпустить сертификат:

```bash
./deploy/prod/compose.sh --profile certbot run --rm certbot
```

4. После выдачи можно поставить:

```text
GATEWAY_MODE=https
```

5. Перезапустить gateway:

```bash
./deploy/prod/compose.sh up -d gateway
```

Если TLS завершает Cloudflare/внешний балансировщик, можно оставить `GATEWAY_MODE=http`, но внешний прокси обязан передавать корректный `Host`.

## Compose-файлы

```text
deploy/prod/compose/
  00-storage.yaml          PostgreSQL, RabbitMQ, MinIO
  10-apps-gateway.yaml     gateway, web, web-ct
  20-core-services.yaml    identity, education, content, tasks, quiz, solutions, rating-worker
  30-execution.yaml        execution-api, execution-worker, runners
  40-ai-and-analyzers.yaml ai-api, ai-worker, analyzers
  50-integrations.yaml     support, minecraft, files, notifications, observability, bots
  80-watchtower.yaml       automatic image updates from GHCR
  90-certbot.yaml          optional certbot profile
```

## Security checklist

`deploy.sh` стопает запуск, если:

- остались `CHANGE_ME`;
- слабый `JWT_SIGNING_KEY` или internal keys;
- `BOOTSTRAP_FIRST_USER_IS_ADMIN=true`;
- `ASPNETCORE_ENVIRONMENT` не `Production`;
- `ENSURE_CREATED=true`;
- наружу проброшены лишние ports;
- Docker Compose config невалидный.

Наружу публикуется только gateway. Storage-порты по умолчанию привязаны к `127.0.0.1`. Остальные API доступны только внутри Docker-сети.

## Полезные команды

```bash
./deploy/prod/compose.sh ps
./deploy/prod/compose.sh logs -f --tail=200
./deploy/prod/compose.sh logs -f gateway identity-api tasks-api solutions-api
./deploy/prod/compose.sh restart gateway
./deploy/prod/compose.sh pull && ./deploy/prod/compose.sh up -d
```

Для сохранения startup-логов:

```bash
./deploy/prod/compose.sh up-logs
```

## Redis cache

The compose stack includes Redis. Backend services receive `ConnectionStrings__Redis` and cache hot metadata such as user summaries, course metadata and assignment summaries. See `docs/operations/redis-cache.md`.

Recommended one-time Redis host tuning:

```bash
echo "vm.overcommit_memory=1" | sudo tee /etc/sysctl.d/99-taskforge-redis.conf
sudo sysctl --system
```


## Image analyzer model cache

`image-analyzer` uses a persistent host cache directory `.runtime/image-analyzer-model-cache` for OpenCLIP/HuggingFace weights.
Do not remove this directory during normal updates; otherwise the analyzer will download the CLIP weights again.

## OJ image/config updates

Runner and `code-analyzer` images are excluded from automatic Watchtower replacement by default (`WATCHTOWER_OJ_ENABLE=false`). Their security contract includes RSA key mounts and other Compose settings, so updating only an image can break a live runner while leaving the old container configuration in place.

Apply OJ changes through the repository scripts:

```bash
./deploy/prod/repair-oj.sh
```

Set `WATCHTOWER_OJ_ENABLE=true` only when a release changes binaries without changing environment variables, secrets, mounts, networks, limits, or security options.
