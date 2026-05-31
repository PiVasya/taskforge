# Миграции нужны для execution/api

Миграции в этом архиве **специально не сгенерированы**.

Сервис должен владеть собственной БД:

```text
taskforge_execution
```

После переноса моделей из `extracted/` в нормальные Domain/Application/Infrastructure слои сгенерировать миграции здесь:

```bash
dotnet ef migrations add InitialExecutionSchema \
  --project services/execution/api/TaskForge.Execution.Api.csproj \
  --startup-project services/execution/api/TaskForge.Execution.Api.csproj \
  --output-dir Data/Migrations
```

Миграции не должны применяться из API при каждом старте. Для Compose/Kubernetes нужен отдельный migrator job.
