# TaskForge real microservices cut

Это целевой микросервисный апдейт проекта без Python runtime-сервисов, кроме разрешённого `services/analyzers/image-analyzer`.

## Запуск dev

```bash
cp deploy/dev/.env.example deploy/dev/.env
./deploy/dev/compose.sh up --build
```

Gateway:

```text
http://localhost:8080
http://taskforge.local:8080      # если прописал hosts
http://ct.taskforge.local:8080   # CT ветка, если прописал hosts
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

## Важно

- Миграции не генерировались.
- Автоприменение миграций оставлено и управляется `MIGRATE_ON_STARTUP=true/false`.
- В каждом сервисе-владельце БД есть `MIGRATIONS_REQUIRED.md`.
- Старые исходники core API не выброшены, а разложены по `extracted/` внутри доменных сервисов.
- `extracted/` исключены из компиляции сервисов и нужны как migration-reference, чтобы не потерять старую логику.
- Раннеры stateless и живут внутри `services/execution/runners`.
- Запуск кода должен идти через `execution-api` + очередь + `execution-worker`.
- AI теперь имеет отдельную границу `ai-api` + `ai-worker` + БД `taskforge_ai`.
- Рейтинг вынесен в materialized read-model: `UserRatings` / `LeaderboardEntries` + `rating-worker`.

## Проверка структуры

```bash
scripts/verify-structure.sh
```

Проверяет YAML, split production compose, Dockerfile paths, отсутствие Python вне `image-analyzer`, отсутствие EF `Migrations/`, наличие `MIGRATIONS_REQUIRED.md`, исключение `extracted/` из компиляции и Go runner tests.

## Runtime compatibility note

The frontend keeps the legacy `/api/...` contract. Gateway splits those paths to the appropriate microservices.
If you update from an older generated archive, regenerate EF migrations because several service schemas changed:

```bash
./scripts/generate-migrations.sh ProjectLaunch
```

For a clean local DB:

```bash
./deploy/dev/compose.sh down --remove-orphans -v
./deploy/dev/compose.sh up-logs --build
```
