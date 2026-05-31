# Миграции нужны для solutions

Миграции в этом архиве **специально не сгенерированы**.

Сервис должен владеть собственной БД:

```text
taskforge_solutions
```

После переноса моделей из `extracted/` в нормальные Domain/Application/Infrastructure слои сгенерировать миграции здесь:

```bash
dotnet ef migrations add InitialSolutionsSchema \
  --project services/solutions/api/TaskForge.Solutions.Api.csproj \
  --startup-project services/solutions/api/TaskForge.Solutions.Api.csproj \
  --output-dir Data/Migrations
```

Миграции не должны применяться из API при каждом старте. Для Compose/Kubernetes нужен отдельный migrator job.
