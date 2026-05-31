# Где генерить миграции потом

Миграции в архиве не генерировались.

Генерить только в сервисах-владельцах БД:

```text
services/identity/api
services/education/api
services/content/api
services/tasks/assignment-api
services/tasks/quiz-api
services/solutions/api
services/execution/api
services/ai/api
services/support/api
services/minecraft/api
services/files/api
services/notifications/api
services/observability/api
services/bots/telegram-quiz-bot, если оставляем локальное состояние бота
```

Не генерить миграции здесь:

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

## Автоприменение

Автоприменение миграций оставлено и управляется переменной:

```text
MIGRATE_ON_STARTUP=true
```

Для Docker Compose это удобно: ты сам генерируешь миграции, кладёшь их в нужный сервис, а сервис применяет их при старте.

Для Kubernetes/replicas позже лучше заменить startup-migrate на отдельный migrator job, чтобы несколько replicas одного API не применяли одну миграцию одновременно.
