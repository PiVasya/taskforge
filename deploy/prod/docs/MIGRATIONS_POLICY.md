# Политика миграций

Миграции в архиве не сгенерированы. Файлы `MIGRATIONS_REQUIRED.md` лежат только в сервисах-владельцах БД.

Генерацию миграций делает только владелец проекта. Архив не содержит сгенерированных EF migration classes.

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

## Где генерировать миграции

- `services/identity/api`
- `services/education/api`
- `services/content/api`
- `services/tasks/assignment-api`
- `services/tasks/quiz-api`
- `services/solutions/api`
- `services/execution/api`
- `services/ai/api`
- `services/support/api`
- `services/minecraft/api`
- `services/files/api`
- `services/notifications/api`
- `services/observability/api`
- `services/bots/telegram-quiz-bot`, если оставляется локальное состояние бота

## Важное для будущего Kubernetes

Для одного Docker Compose production-сервера автоприменение допустимо.

Для Kubernetes и replicas лучше заменить startup-migrate на отдельные migration jobs/bundles:

```text
identity-migrator -> identity-api replicas
solutions-migrator -> solutions-api replicas
ai-migrator -> ai-api replicas
```

Иначе две replicas одного API могут одновременно попытаться применить одну миграцию.
