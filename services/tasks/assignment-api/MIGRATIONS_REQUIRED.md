# Миграции нужны для tasks/assignment-api

Миграции в этом архиве **специально не сгенерированы**.

Сервис должен владеть собственной БД:

```text
taskforge_tasks
```

После переноса моделей из `extracted/` в нормальные Domain/Application/Infrastructure слои сгенерировать миграции здесь:

```bash
dotnet ef migrations add InitialTasksSchema \
  --project services/tasks/assignment-api/TaskForge.Tasks.Api.csproj \
  --startup-project services/tasks/assignment-api/TaskForge.Tasks.Api.csproj \
  --output-dir Data/Migrations
```

Миграции не должны применяться из API при каждом старте. Для Compose/Kubernetes нужен отдельный migrator job.
