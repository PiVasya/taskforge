# Миграции нужны для ai/api

Миграции в этом архиве **специально не сгенерированы**.

Сервис должен владеть собственной БД:

```text
taskforge_ai
```

После переноса моделей из `extracted/` в нормальные Domain/Application/Infrastructure слои сгенерировать миграции здесь:

```bash
dotnet ef migrations add InitialAiSchema \
  --project services/ai/api/TaskForge.Ai.Api.csproj \
  --startup-project services/ai/api/TaskForge.Ai.Api.csproj \
  --output-dir Data/Migrations
```

Миграции не должны применяться из API при каждом старте. Для Compose/Kubernetes нужен отдельный migrator job.
