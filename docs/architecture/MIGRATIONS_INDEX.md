# Миграции микросервисов

В `develop` миграции хранятся в репозитории.

## Текущий baseline

```text
InitialMicroserviceSchema
```

## DB-owning сервисы

```text
services/identity/api/Migrations
services/education/api/Migrations
services/content/api/Migrations
services/tasks/assignment-api/Migrations
services/tasks/quiz-api/Migrations
services/solutions/api/Migrations
services/execution/api/Migrations
services/ai/api/Migrations
services/support/api/Migrations
services/minecraft/api/Migrations
services/files/api/Migrations
services/notifications/api/Migrations
services/observability/api/Migrations
services/bots/telegram-quiz-bot/Data/Migrations
```

## Не генерить миграции здесь

```text
apps/*
services/execution/worker
services/execution/runners/*
services/solutions/rating-worker
services/ai/worker
services/analyzers/*
services/bots/support-bot
plugins/*
```

## Обычная команда

```bash
./scripts/generate-migrations.sh AddMeaningfulSchemaChange
```

Скрипт безопасный: сначала проверяет `has-pending-model-changes`, потом создаёт миграцию только при реальном изменении модели.

## Принудительная команда

```bash
./scripts/generate-migrations-force.sh MigrationName
```

Использовать редко. Может создать пустые миграции.

## Автоприменение

Docker Compose применяет миграции на старте сервисов, если включено:

```text
MIGRATE_ON_STARTUP=true
```

Для Kubernetes/replicas позже лучше заменить startup-migrate на отдельный migrator job, чтобы несколько replicas одного API не применяли одну миграцию одновременно.
