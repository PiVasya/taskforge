# Миграции нужны для content-service

Сервис владеет БД `taskforge_content`.

В архиве оставлены старые миграции из исходного сервиса как история, но новые миграции в этом апдейте не генерировались.

После финального переноса моделей проверить snapshot и выполнить:

```bash
dotnet ef migrations add SplitContentSchema \
  --project services/content/api/learning-content-service.csproj \
  --startup-project services/content/api/learning-content-service.csproj \
  --output-dir Migrations
```
