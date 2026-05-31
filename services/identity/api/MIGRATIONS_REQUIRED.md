# Миграции нужны для identity

Миграции в этом архиве **специально не сгенерированы**.

Сервис должен владеть собственной БД:

```text
taskforge_identity
```

После переноса моделей из `extracted/` в нормальные Domain/Application/Infrastructure слои сгенерировать миграции здесь:

```bash
dotnet ef migrations add InitialIdentitySchema \
  --project services/identity/api/TaskForge.Identity.Api.csproj \
  --startup-project services/identity/api/TaskForge.Identity.Api.csproj \
  --output-dir Data/Migrations
```

Миграции не должны применяться из API при каждом старте. Для Compose/Kubernetes нужен отдельный migrator job.
