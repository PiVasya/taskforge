# Политика миграций

В `develop` EF migrations хранятся в репозитории.

Правило проекта:

```text
один DB-owning сервис = один DbContext = одна папка Migrations = своя история миграций
```

Текущий baseline называется:

```text
InitialMicroserviceSchema
```

## Где лежат миграции

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

## Как добавлять новые миграции

Обычная разработка:

```bash
./scripts/generate-migrations.sh AddMeaningfulSchemaChange
```

Этот скрипт проверяет pending model changes и создаёт миграцию только для тех DbContext, где модель реально изменилась.

Принудительный режим:

```bash
./scripts/generate-migrations-force.sh MigrationName
```

`force`-скрипт нужен редко. Он может создать пустые миграции, поэтому не используй его для обычной разработки.

## Автоприменение

Автоприменение миграций оставлено и управляется переменной:

```text
MIGRATE_ON_STARTUP=true
```

Она прокинута в DB-owning сервисы как:

```text
Database__MigrateOnStartup=true
```

Для `telegram-quiz-bot` она прокинута как:

```text
TelegramQuiz__ApplyMigrationsOnStartup=true
```

`ENSURE_CREATED=false` по умолчанию, чтобы не смешивать `EnsureCreated` и нормальные EF migrations.

## Важное для будущего Kubernetes

Для одного Docker Compose production-сервера startup auto-migrate допустим.

Для Kubernetes и replicas лучше заменить startup-migrate на отдельные migration jobs/bundles:

```text
identity-migrator -> identity-api replicas
solutions-migrator -> solutions-api replicas
ai-migrator -> ai-api replicas
```

Иначе две replicas одного API могут одновременно попытаться применить одну миграцию.
