# TaskForge real microservices cut

Целевой микросервисный апдейт проекта без Python runtime-сервисов, кроме разрешённого `services/analyzers/image-analyzer`.

## Запуск dev

```bash
cp deploy/dev/.env.example deploy/dev/.env
./deploy/dev/compose.sh up-logs --build
```

Gateway по умолчанию:

```text
http://localhost:18080
```

Если нужен старый порт `8080`, поменяй в `deploy/dev/.env`:

```text
DEV_GATEWAY_HTTP_PORT=8080
```

## Production запуск

Production-схема лежит в `deploy/prod/` и разбита на несколько compose-файлов по направлениям, а не в один огромный YAML.

```bash
cp deploy/prod/.env.example deploy/prod/.env
# edit deploy/prod/.env

./deploy/prod/compose.sh pull
./deploy/prod/compose.sh up -d
```

Split-файлы:

```text
deploy/prod/compose/00-storage.yaml
deploy/prod/compose/10-apps-gateway.yaml
deploy/prod/compose/20-core-services.yaml
deploy/prod/compose/30-execution.yaml
deploy/prod/compose/40-ai-and-analyzers.yaml
deploy/prod/compose/50-integrations.yaml
deploy/prod/compose/90-certbot.yaml
```

## Структура

```text
apps/                 web, web-ct, gateway
services/             доменные сервисы
plugins/minecraft/    Minecraft plugins
contracts/            HTTP/event contracts
infrastructure/       postgres init, k8s заготовки
tools/                database splitter
docs/                 архитектура и эксплуатация
deploy/               dev/prod split compose окружения
```

## Миграции

В `develop` миграции теперь являются частью исходного кода.

Правило:

```text
один DB-owning сервис = один DbContext = одна папка Migrations = своя история миграций
```

В репозитории есть чистый baseline:

```text
InitialMicroserviceSchema
```

Для новых изменений схемы используй безопасный скрипт:

```bash
./scripts/generate-migrations.sh AddMeaningfulSchemaChange
```

Он сначала вызывает `dotnet ef migrations has-pending-model-changes` и создаёт миграцию только если EF видит реальные изменения модели.

Если нужно принудительно создать миграцию, есть отдельный опасный скрипт:

```bash
./scripts/generate-migrations-force.sh MigrationName
```

Его не надо использовать каждый день, потому что он может создать пустые миграции.

Автоприменение миграций в compose оставлено и управляется:

```text
MIGRATE_ON_STARTUP=true
ENSURE_CREATED=false
```

## Важно

- Старые исходники core API не выброшены, а разложены по `extracted/` внутри доменных сервисов.
- `extracted/` исключены из компиляции сервисов и нужны как migration-reference, чтобы не потерять старую логику.
- Раннеры stateless и живут внутри `services/execution/runners`.
- Запуск кода идёт через `execution-api` + очередь + `execution-worker`.
- AI имеет отдельную границу `ai-api` + `ai-worker` + БД `taskforge_ai`.
- Рейтинг вынесен в materialized read-model: `UserRatings` / `LeaderboardEntries` + `rating-worker`.
- Root `.dockerignore` добавлен, чтобы Docker build context не тащил локальные логи, `bin/obj`, `node_modules`, архивы и дампы.

## Проверка структуры

```bash
./scripts/verify-structure.sh
```

Проверяет YAML, split compose, Dockerfile paths, отсутствие Python вне `image-analyzer`, наличие миграций и `ModelSnapshot` у DB-owning сервисов, отсутствие пустых миграций, отсутствие старых `MIGRATIONS_REQUIRED.md`, healthcheck'и, `.dockerignore`, исключение `extracted/` из компиляции и Go runner tests.

## Frontend compatibility note

Фронт сохраняет старый `/api/...` контракт. Gateway делит эти пути по микросервисам:

```text
/api/auth/*        -> identity-api
/api/courses*      -> education-api
/api/assignments*  -> tasks-api / solutions-api по назначению
/api/compiler/*    -> execution-api
/api/agent/*       -> ai-api
```

Для чистого локального dev-старта:

```bash
./deploy/dev/compose.sh down --remove-orphans -v
./deploy/dev/compose.sh up-logs --build
```
