# Миграции нужны для minecraft

Миграции в этом архиве **специально не сгенерированы**.

Сервис должен владеть собственной БД:

```text
taskforge_minecraft
```

После переноса моделей из `extracted/` в нормальные Domain/Application/Infrastructure слои сгенерировать миграции здесь:

```bash
dotnet ef migrations add InitialMinecraftSchema \
  --project services/minecraft/api/TaskForge.Minecraft.Api.csproj \
  --startup-project services/minecraft/api/TaskForge.Minecraft.Api.csproj \
  --output-dir Data/Migrations
```

Миграции не должны применяться из API при каждом старте. Для Compose/Kubernetes нужен отдельный migrator job.
