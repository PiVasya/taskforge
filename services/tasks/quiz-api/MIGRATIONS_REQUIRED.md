# Миграции нужны для quiz/tasks-service

Сервис владеет частью БД `taskforge_tasks`.

Старые миграции сохранены, новые не генерировались.

```bash
dotnet ef migrations add SplitQuizTaskSchema \
  --project services/tasks/quiz-api/quiz-task-service.csproj \
  --startup-project services/tasks/quiz-api/quiz-task-service.csproj \
  --output-dir Migrations
```
