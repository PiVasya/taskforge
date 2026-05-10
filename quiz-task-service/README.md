# quiz-task-service

Отдельный микросервис для мини-задач, тестов и ЦТ/ЦЭ-заданий: A1, A2, B10, B11 и т.п.

## БД

По умолчанию использует отдельную базу в том же Postgres-контейнере:

```text
taskforge_quiz
```

Сервис сам создаёт таблицы через `EnsureCreated` на старте. Для прод-эксплуатации позже лучше перейти на EF migrations.

## Основные маршруты

```http
GET  /health/live
GET  /health/ready
GET  /api/quiz/tasks?sectionCode=A1
GET  /api/quiz/tasks/{idOrSlug}
POST /api/quiz/tasks/{id}/attempts
GET  /api/quiz/me/progress
POST /api/admin/quiz/tasks
```

Сервис хранит версии задания. Попытка пользователя сохраняет `TaskVersionId`, поэтому старые попытки не ломаются после редактирования задания.
