# Миграции нужны для notifications

Миграции в этом архиве **специально не сгенерированы**.

Сервис должен владеть собственной БД:

```text
taskforge_notifications
```

После переноса моделей из `extracted/` в нормальные Domain/Application/Infrastructure слои сгенерировать миграции здесь:

```bash
dotnet ef migrations add InitialNotificationsSchema \
  --project services/notifications/api/TaskForge.Notifications.Api.csproj \
  --startup-project services/notifications/api/TaskForge.Notifications.Api.csproj \
  --output-dir Data/Migrations
```

Миграции не должны применяться из API при каждом старте. Для Compose/Kubernetes нужен отдельный migrator job.
